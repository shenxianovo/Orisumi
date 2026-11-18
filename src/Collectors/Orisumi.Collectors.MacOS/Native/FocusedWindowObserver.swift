import AppKit
import ApplicationServices
import CoreFoundation

final class FocusedWindowObserver {
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var pid: pid_t?
    private var application: AXUIElement?
    private var observer: AXObserver?
    private var window: AXUIElement?
    private var title: String?
    private var hasSample = false
    private var pendingKind: WindowEventKind = .snapshot
    private var schedule = WindowObservationSchedule()
    private var lastSubscriptionAttempt: TimeInterval?

    init(database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.database = database
        self.failed = failed
    }

    deinit { detach() }

    func watch(_ app: NSRunningApplication?, kind: WindowEventKind) throws {
        if kind == .focusChanged, let pid, app?.processIdentifier == pid {
            attachObserver()
            try sample()
            return
        }
        detach()
        guard let pid = app?.processIdentifier, pid > 0 else {
            try suspend(reason: "no_foreground_application")
            return
        }
        self.pid = pid
        pendingKind = kind
        let application = AXUIElementCreateApplication(pid)
        self.application = application
        AXUIElementSetMessagingTimeout(application, 0.25)
        attachObserver()
        try sample()
    }

    func reconcile(_ app: NSRunningApplication?) throws {
        if app?.processIdentifier != pid {
            try watch(app, kind: .snapshot)
        } else if schedule.shouldRead(at: ProcessInfo.processInfo.systemUptime) {
            attachObserver()
            try sample()
        }
    }

    private func attachObserver() {
        guard let application, let pid else { return }
        let uptime = ProcessInfo.processInfo.systemUptime
        if let lastSubscriptionAttempt, uptime - lastSubscriptionAttempt < 60 { return }
        lastSubscriptionAttempt = uptime
        if observer != nil {
            if !schedule.focusNotification { schedule.focusNotification = addNotification(kAXFocusedWindowChangedNotification, to: application) }
            if !schedule.titleNotification, let window { schedule.titleNotification = addNotification(kAXTitleChangedNotification, to: window) }
            return
        }
        var created: AXObserver?
        let result = AXObserverCreate(pid, { observer, _, _, context in
            guard let context else { return }
            let target = Unmanaged<FocusedWindowObserver>.fromOpaque(context).takeUnretainedValue()
            guard let current = target.observer, CFEqual(observer, current),
                  NSWorkspace.shared.frontmostApplication?.processIdentifier == target.pid
            else { return }
            do {
                try target.sample()
            } catch {
                target.failed(error)
            }
        }, &created)
        if result == .success, let created {
            observer = created
            CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .commonModes)
            schedule.focusNotification = addNotification(kAXFocusedWindowChangedNotification, to: application)
            if let window { schedule.titleNotification = addNotification(kAXTitleChangedNotification, to: window) }
        }
    }

    func detach() {
        if let observer {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
        }
        observer = nil
        application = nil
        pid = nil
        window = nil
        title = nil
        hasSample = false
        schedule = WindowObservationSchedule()
        lastSubscriptionAttempt = nil
    }

    func suspend(reason: String) throws {
        detach()
        try database.appendChannel("windows", available: false, reason: reason)
        try database.appendChannel("window_title", available: false, reason: reason)
    }

    private func addNotification(_ name: String, to element: AXUIElement) -> Bool {
        guard let observer else { return false }
        let result = AXObserverAddNotification(observer, element, name as CFString, Unmanaged.passUnretained(self).toOpaque())
        return result == .success || result == .notificationAlreadyRegistered
    }

    private func sample() throws {
        guard let pid, let application else { return }
        let observedAt = Date()
        schedule.lastRead = ProcessInfo.processInfo.systemUptime
        var focusedValue: CFTypeRef?
        let focusResult = AXUIElementCopyAttributeValue(application, kAXFocusedWindowAttribute as CFString, &focusedValue)
        let focusedWindow: AXUIElement?
        if focusResult == .success, let focusedValue, CFGetTypeID(focusedValue) == AXUIElementGetTypeID() {
            focusedWindow = (focusedValue as! AXUIElement)
        } else if focusResult == .noValue {
            focusedWindow = nil
        } else {
            try database.appendChannel("windows", available: false, reason: "read_failed", at: observedAt)
            try database.appendChannel("window_title", available: false, reason: "focus_unavailable", at: observedAt)
            // Record that the last known focus no longer holds; the next successful read sets a new initial value.
            if hasSample { try database.appendWindow(.readFailed, at: observedAt, pid: pid, title: nil, hasWindow: nil) }
            hasSample = false
            pendingKind = .snapshot
            schedule.readSucceeded = false
            return
        }
        try database.appendChannel("windows", available: true, reason: "sample_received", at: observedAt)

        let windowChanged: Bool
        if let window, let focusedWindow {
            windowChanged = !CFEqual(window, focusedWindow)
        } else {
            windowChanged = (window != nil) != (focusedWindow != nil)
        }
        if windowChanged {
            if let observer, let window {
                AXObserverRemoveNotification(observer, window, kAXTitleChangedNotification as CFString)
            }
            if let focusedWindow {
                schedule.titleNotification = addNotification(kAXTitleChangedNotification, to: focusedWindow)
            }
        }

        var focusedTitle: String?
        var titleReadSucceeded = true
        if let focusedWindow {
            var titleValue: CFTypeRef?
            let titleResult = AXUIElementCopyAttributeValue(focusedWindow, kAXTitleAttribute as CFString, &titleValue)
            if titleResult == .success {
                focusedTitle = titleValue as? String
            } else if titleResult == .noValue || titleResult == .attributeUnsupported {
                focusedTitle = "" // The window has no title; NULL is kept for a failed read.
            } else {
                titleReadSucceeded = false
            }
        }
        try database.appendChannel("window_title", available: titleReadSucceeded, reason: titleReadSucceeded ? "sample_received" : "read_failed", at: observedAt)
        try database.appendMethod("window_focus", mode: schedule.focusNotification ? "notification" : "snapshot", at: observedAt)
        if focusedWindow != nil {
            try database.appendMethod("window_title", mode: schedule.titleNotification ? "notification" : "snapshot", at: observedAt)
        }
        if !hasSample {
            try database.appendWindow(pendingKind, at: observedAt, pid: pid, title: focusedTitle, hasWindow: focusedWindow != nil)
        } else if windowChanged {
            try database.appendWindow(.focusChanged, at: observedAt, pid: pid, title: focusedTitle, hasWindow: focusedWindow != nil)
        } else if focusedTitle != title {
            try database.appendWindow(.titleChanged, at: observedAt, pid: pid, title: focusedTitle, hasWindow: focusedWindow != nil)
        }
        window = focusedWindow
        title = focusedTitle
        hasSample = true
        schedule.hasWindow = focusedWindow != nil
        schedule.readSucceeded = titleReadSucceeded
    }
}
