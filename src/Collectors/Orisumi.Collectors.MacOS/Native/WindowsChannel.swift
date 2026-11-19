import Foundation

enum WindowRead<Value> {
    case value(Value)
    case none
    case failed
}

// The accessibility view of one application; an AXUIElement with an AXObserver in production.
protocol WindowApplication: AnyObject {
    /// Subscribes to focused-window changes; false when the application does not deliver them.
    func watchFocus() -> Bool
    func watchTitle(of window: AnyObject) -> Bool
    func unwatchTitle(of window: AnyObject)
    func focusedWindow() -> WindowRead<AnyObject>
    /// `.none` means the window has no title.
    func title(of window: AnyObject) -> WindowRead<String?>
    func isSameWindow(_ first: AnyObject, _ second: AnyObject) -> Bool
}

struct WindowSource {
    let permitted: () -> Bool
    /// Opens the application with this PID; `changed` runs on each focus or title notification.
    let open: (pid_t, @escaping () -> Void) -> WindowApplication
}

// The focused window of the foreground application and its title.
final class WindowsChannel: CaptureChannel {
    private let source: WindowSource
    private let applications: ApplicationsChannel
    private let events: ObservationEvents
    private let failed: (Error) -> Void
    private var permission = CapturePermissionState()
    private var listening = false
    private var pid: pid_t?
    private var application: WindowApplication?
    private var window: AnyObject?
    private var title: String?
    private var hasSample = false
    private var pendingKind: WindowEventKind = .snapshot
    private var schedule = WindowObservationSchedule()
    private var lastSubscriptionAttempt: TimeInterval?

    init(source: WindowSource, applications: ApplicationsChannel, events: ObservationEvents, failed: @escaping (Error) -> Void) {
        self.source = source
        self.applications = applications
        self.events = events
        self.failed = failed
        applications.onFrontmostChange { [weak self] app in
            guard let self, self.listening, self.permission.granted else { return }
            do {
                if self.applications.frontmost?.pid == app?.pid {
                    try self.watch(app?.pid, kind: .focusChanged)
                } else {
                    // The activation notification can arrive before the application is frontmost.
                    DispatchQueue.main.async { [weak self] in
                        guard let self, self.listening, self.permission.granted,
                              let pid = app?.pid, self.applications.frontmost?.pid == pid else { return }
                        do { try self.watch(pid, kind: .focusChanged) } catch { self.failed(error) }
                    }
                }
            } catch {
                self.failed(error)
            }
        }
    }

    func start() throws {
        listening = true
        try checkPermission(initial: true)
    }

    func interrupt(reason: String) throws {
        listening = false
        try suspend(reason: reason)
    }

    func resume() throws {
        listening = true
        try checkPermission(initial: true)
    }

    func reconcile() throws {
        try checkPermission(initial: false)
        guard permission.granted else { return }
        let frontmost = applications.frontmost?.pid
        if frontmost != pid {
            try watch(frontmost, kind: .snapshot)
        } else if schedule.shouldRead(at: ProcessInfo.processInfo.systemUptime) {
            attach()
            try sample()
        }
    }

    func stop() throws {
        listening = false
        detach()
    }

    private func checkPermission(initial: Bool) throws {
        let previous = permission.granted
        let granted = source.permitted()
        try permission.observe(granted) { event in
            try events.appendPermission(.accessibility, kind: event, at: Date())
        }
        if !granted {
            if initial || previous { try suspend(reason: "permission_denied") }
        } else if initial || !previous {
            try watch(applications.frontmost?.pid, kind: .snapshot)
        }
    }

    private func watch(_ frontmost: pid_t?, kind: WindowEventKind) throws {
        if kind == .focusChanged, let pid, frontmost == pid {
            attach()
            try sample()
            return
        }
        detach()
        guard let frontmost, frontmost > 0 else {
            try suspend(reason: "no_foreground_application")
            return
        }
        pid = frontmost
        pendingKind = kind
        application = source.open(frontmost) { [weak self] in
            guard let self, self.listening, self.applications.frontmost?.pid == self.pid else { return }
            do { try self.sample() } catch { self.failed(error) }
        }
        attach()
        try sample()
    }

    private func attach() {
        guard let application else { return }
        let uptime = ProcessInfo.processInfo.systemUptime
        if let lastSubscriptionAttempt, uptime - lastSubscriptionAttempt < 60 { return }
        lastSubscriptionAttempt = uptime
        if !schedule.focusNotification { schedule.focusNotification = application.watchFocus() }
        if !schedule.titleNotification, let window { schedule.titleNotification = application.watchTitle(of: window) }
    }

    private func detach() {
        application = nil
        pid = nil
        window = nil
        title = nil
        hasSample = false
        schedule = WindowObservationSchedule()
        lastSubscriptionAttempt = nil
    }

    private func suspend(reason: String) throws {
        detach()
        try events.appendChannel("windows", available: false, reason: reason)
        try events.appendChannel("window_title", available: false, reason: reason)
    }

    private func sample() throws {
        guard let pid, let application else { return }
        let observedAt = Date()
        schedule.lastRead = ProcessInfo.processInfo.systemUptime
        let focusedWindow: AnyObject?
        switch application.focusedWindow() {
        case let .value(window): focusedWindow = window
        case .none: focusedWindow = nil
        case .failed:
            try events.appendChannel("windows", available: false, reason: "read_failed", at: observedAt)
            try events.appendChannel("window_title", available: false, reason: "focus_unavailable", at: observedAt)
            // Record that the last known focus no longer holds; the next successful read sets a new initial value.
            if hasSample { try events.appendWindow(.readFailed, at: observedAt, title: nil, hasWindow: nil) }
            hasSample = false
            pendingKind = .snapshot
            schedule.readSucceeded = false
            return
        }
        try events.appendChannel("windows", available: true, reason: "sample_received", at: observedAt)

        let windowChanged: Bool
        if let window, let focusedWindow {
            windowChanged = !application.isSameWindow(window, focusedWindow)
        } else {
            windowChanged = (window != nil) != (focusedWindow != nil)
        }
        if windowChanged {
            if let window { application.unwatchTitle(of: window) }
            if let focusedWindow { schedule.titleNotification = application.watchTitle(of: focusedWindow) }
        }

        var focusedTitle: String?
        var titleReadSucceeded = true
        if let focusedWindow {
            switch application.title(of: focusedWindow) {
            case let .value(value): focusedTitle = value
            case .none: focusedTitle = "" // The window has no title; NULL is kept for a failed read.
            case .failed: titleReadSucceeded = false
            }
        }
        try events.appendChannel("window_title", available: titleReadSucceeded, reason: titleReadSucceeded ? "sample_received" : "read_failed", at: observedAt)
        try events.appendMethod("window_focus", mode: schedule.focusNotification ? "notification" : "snapshot", at: observedAt)
        if focusedWindow != nil {
            try events.appendMethod("window_title", mode: schedule.titleNotification ? "notification" : "snapshot", at: observedAt)
        }
        if !hasSample || windowChanged {
            let kind = hasSample ? .focusChanged : pendingKind
            let instance: String?
            if focusedWindow != nil {
                guard let observed = try applications.frontmostInstance(pid: pid, at: observedAt) else { return }
                instance = observed
            } else {
                instance = nil
            }
            try events.appendWindow(kind, at: observedAt, instance: instance, title: focusedTitle, hasWindow: focusedWindow != nil)
        } else if focusedTitle != title {
            try events.appendWindow(.titleChanged, at: observedAt, title: focusedTitle, hasWindow: true)
        }
        window = focusedWindow
        title = focusedTitle
        hasSample = true
        schedule.hasWindow = focusedWindow != nil
        schedule.readSucceeded = titleReadSucceeded
    }
}
