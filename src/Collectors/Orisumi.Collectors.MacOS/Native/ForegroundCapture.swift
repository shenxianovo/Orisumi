import AppKit
import ApplicationServices
import CoreFoundation
import CoreGraphics

private extension ForegroundApplication {
    init(_ app: NSRunningApplication) {
        self.init(bundleID: app.bundleIdentifier, name: app.localizedName, pid: app.processIdentifier)
    }
}

func captureForeground(in database: ForegroundDatabase, seconds: Int?, captureWindows: Bool,
                       afkThresholdMS: Int64, captureInput: Bool) throws {
    let workspace = NSWorkspace.shared
    let center = workspace.notificationCenter
    let subscriptions: [(Notification.Name, EventKind)] = [
        (NSWorkspace.didActivateApplicationNotification, .activated),
        (NSWorkspace.willSleepNotification, .sleep),
        (NSWorkspace.didWakeNotification, .wake),
        (NSWorkspace.sessionDidBecomeActiveNotification, .sessionActive),
        (NSWorkspace.sessionDidResignActiveNotification, .sessionInactive),
    ]

    let loop = CFRunLoopGetMain()!
    var failure: Error?
    var stopping = false
    func fail(_ error: Error) {
        guard !stopping else { return }
        failure = error
        stopping = true
        CFRunLoopStop(loop)
    }
    let windowObserver = captureWindows ? FocusedWindowObserver(database: database, failed: fail) : nil
    var asleep = false
    var sessionInactive = false
    var idle = ActivityIdleTracker(threshold: TimeInterval(afkThresholdMS) / 1000)
    var idleTimer: CFRunLoopTimer?
    var inputObserver: InputEventObserver?
    var windowPermission = CapturePermissionState()
    var inputPermission = CapturePermissionState()
    var reportedTapFailure = false
    var lifecycleObserver: ApplicationLifecycleObserver?

    func recordIdle(_ change: (AFKEventKind, AFKState, Int64)?) throws {
        if let (kind, state, idleMS) = change {
            try database.appendAFK(kind, at: Date(), state: state, idleMS: idleMS)
        }
    }

    func resetIdle() {
        if let idleTimer { CFRunLoopTimerInvalidate(idleTimer) }
        idleTimer = nil
        idle.reset()
    }

    func scheduleIdle() {
        guard idleTimer == nil, inputObserver != nil, !asleep, !sessionInactive,
              let remaining = idle.remaining(at: ProcessInfo.processInfo.systemUptime) else { return }
        let timer = CFRunLoopTimerCreateWithHandler(kCFAllocatorDefault,
            CFAbsoluteTimeGetCurrent() + max(0.001, remaining), 0, 0, 0) { _ in idleDeadlineReached() }
        idleTimer = timer
        CFRunLoopAddTimer(loop, timer, .commonModes)
    }

    func idleDeadlineReached() {
        idleTimer = nil
        guard !stopping, !asleep, !sessionInactive else { return }
        do {
            try recordIdle(idle.tick(at: ProcessInfo.processInfo.systemUptime))
            scheduleIdle()
        } catch { fail(error) }
    }

    func sampleAFK() throws {
        guard !asleep && !sessionInactive else { return }
        let anyInput = CGEventType(rawValue: UInt32.max)!
        let seconds = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput)
        guard seconds.isFinite, seconds >= 0, seconds < Double(Int64.max) / 1000 else {
            resetIdle()
            try database.appendChannel("afk", available: false, reason: "read_failed")
            return
        }
        try database.appendChannel("afk", available: true, reason: "sample_received")
        try database.appendMethod("afk", mode: inputObserver == nil ? "snapshot" : "notification")
        try recordIdle(idle.observe(idle: seconds, at: ProcessInfo.processInfo.systemUptime))
        scheduleIdle()
    }

    func inputActivity() {
        do {
            try recordIdle(idle.activity(at: ProcessInfo.processInfo.systemUptime))
            scheduleIdle()
        } catch { fail(error) }
    }

    func inputInterrupted() {
        resetIdle()
        do { try sampleAFK() } catch { fail(error) }
    }

    func stopInput() throws {
        let wasListening = inputObserver != nil
        inputObserver?.stop()
        try inputObserver?.flush()
        inputObserver = nil
        if wasListening { resetIdle() }
        if captureInput { try database.appendChannel("input", available: false, reason: "listener_stopped") }
    }

    func sampleInputPermission() throws {
        guard captureInput else { return }
        let granted = CGPreflightListenEventAccess()
        try inputPermission.observe(granted) { event in
            try database.appendPermission(.inputMonitoring, kind: event, at: Date())
        }
        if !granted { try stopInput() }
    }

    func handleInputFailure(_ error: Error) {
        guard case InputCaptureError.tapUnavailable = error else {
            fail(error)
            return
        }
        do {
            try stopInput()
            try sampleInputPermission()
        } catch {
            fail(error)
        }
    }

    func refreshPermissions() throws {
        if let windowObserver {
            let granted = AXIsProcessTrusted()
            let previous = windowPermission.granted
            try windowPermission.observe(granted) { event in
                try database.appendPermission(.accessibility, kind: event, at: Date())
            }
            if !granted {
                try windowObserver.suspend(reason: "permission_denied")
            } else if !previous && granted && !asleep && !sessionInactive {
                try windowObserver.watch(workspace.frontmostApplication, kind: .snapshot)
            }
        }

        try sampleInputPermission()
        if captureInput && inputPermission.granted && !asleep && !sessionInactive && inputObserver == nil {
            do {
                inputObserver = try InputEventObserver(database: database, failed: handleInputFailure,
                                                       activity: inputActivity, interrupted: inputInterrupted)
                try database.appendChannel("input", available: true, reason: "listener_started")
                try sampleAFK()
                reportedTapFailure = false
            } catch InputCaptureError.tapUnavailable {
                try database.appendChannel("input", available: false, reason: "tap_unavailable")
                if !reportedTapFailure {
                    fputs("orisumi: input listener unavailable; will retry\n", stderr)
                    reportedTapFailure = true
                }
                try sampleInputPermission()
            }
        }
    }

    var lastTimeZoneID: String?
    func sampleTimeZone(at observedAt: Date) throws {
        let identifier = TimeZone.autoupdatingCurrent.identifier
        guard identifier != lastTimeZoneID else { return }
        try database.appendTimeZone(identifier, at: observedAt)
        lastTimeZoneID = identifier
    }
    let observers = subscriptions.map { name, kind in
        center.addObserver(forName: name, object: nil, queue: .main) { notification in
            guard !stopping else { return }
            let observedAt = Date()
            let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            do {
                if kind == .wake || kind == .sessionActive {
                    try sampleTimeZone(at: observedAt)
                }
                let foregroundApp = kind == .activated ? app.map(ForegroundApplication.init) : nil
                try database.append(kind, at: observedAt, app: foregroundApp)
                if kind == .wake || kind == .sessionActive {
                    try database.append(.snapshot, at: Date(), app: workspace.frontmostApplication.map(ForegroundApplication.init))
                }
                switch kind {
                case .sleep:
                    asleep = true
                    resetIdle()
                    try database.appendChannel("afk", available: false, reason: "session_paused")
                case .sessionInactive:
                    sessionInactive = true
                    resetIdle()
                    try database.appendChannel("afk", available: false, reason: "session_paused")
                case .wake:
                    asleep = false
                    try sampleAFK()
                case .sessionActive:
                    sessionInactive = false
                    try sampleAFK()
                case .activated, .snapshot: break
                }
                if kind == .sleep || kind == .sessionInactive || kind == .wake || kind == .sessionActive {
                    if asleep || sessionInactive {
                        lifecycleObserver?.pause()
                        database.interruptUnverifiedProcesses()
                        try database.appendChannel("applications", available: false, reason: "session_paused")
                    } else {
                        lifecycleObserver?.resume(workspace.runningApplications)
                        try database.appendChannel("applications", available: true, reason: "snapshot_received")
                    }
                    try sampleInputPermission()
                    do {
                        try inputObserver?.setPaused(asleep || sessionInactive)
                    } catch InputCaptureError.tapUnavailable {
                        handleInputFailure(InputCaptureError.tapUnavailable)
                    }
                }
                if let windowObserver, windowPermission.granted {
                    switch kind {
                    case .sleep, .sessionInactive:
                        try windowObserver.suspend(reason: "session_paused")
                    case .wake:
                        if !sessionInactive {
                            try windowObserver.watch(workspace.frontmostApplication, kind: .snapshot)
                        }
                    case .sessionActive:
                        if !asleep {
                            try windowObserver.watch(workspace.frontmostApplication, kind: .snapshot)
                        }
                    case .activated:
                        if !asleep && !sessionInactive, let app {
                            if workspace.frontmostApplication?.processIdentifier == app.processIdentifier {
                                try windowObserver.watch(app, kind: .focusChanged)
                            } else {
                                DispatchQueue.main.async {
                                    guard !stopping && !asleep && !sessionInactive && windowPermission.granted,
                                          workspace.frontmostApplication?.processIdentifier == app.processIdentifier
                                    else { return }
                                    do {
                                        try windowObserver.watch(app, kind: .focusChanged)
                                    } catch {
                                        fail(error)
                                    }
                                }
                            }
                        }
                    case .snapshot: break
                    }
                }
            } catch {
                fail(error)
            }
        }
    }
    defer { observers.forEach(center.removeObserver) }

    let startedAt = Date()
    try database.start(at: startedAt, afkThresholdMS: afkThresholdMS)
    var completed = false
    defer {
        if !completed {
            do { try database.stop(at: Date(), failed: true) }
            catch { fputs("orisumi: could not record failed capture stop: \(error.localizedDescription)\n", stderr) }
        }
    }
    defer { if let idleTimer { CFRunLoopTimerInvalidate(idleTimer) } }
    try sampleTimeZone(at: startedAt)
    let timeZoneObserver = NotificationCenter.default.addObserver(
        forName: .NSSystemTimeZoneDidChange, object: nil, queue: .main
    ) { _ in
        guard !stopping else { return }
        do {
            try sampleTimeZone(at: Date())
        } catch {
            fail(error)
        }
    }
    defer { NotificationCenter.default.removeObserver(timeZoneObserver) }
    lifecycleObserver = ApplicationLifecycleObserver(workspace: workspace, database: database, failed: fail)
    defer { lifecycleObserver?.stop() }
    if let failure { throw failure }
    try database.appendChannel("applications", available: true, reason: "snapshot_received")
    try database.append(.snapshot, at: Date(), app: workspace.frontmostApplication.map(ForegroundApplication.init))
    try sampleAFK()
    try refreshPermissions()
    let refresh = Timer(timeInterval: 1, repeats: true) { _ in
        guard !stopping else { return }
        do {
            try refreshPermissions()
            guard !asleep && !sessionInactive else { return }
            if inputObserver == nil { try sampleAFK() }
            if windowPermission.granted {
                try windowObserver?.reconcile(workspace.frontmostApplication)
            }
        } catch {
            fail(error)
        }
    }
    RunLoop.main.add(refresh, forMode: .common)
    defer { refresh.invalidate() }

    DispatchQueue.global().async {
        while let command = readLine(), command != "stop" {}
        DispatchQueue.main.async { CFRunLoopStop(loop) }
    }
    if let seconds {
        DispatchQueue.main.asyncAfter(deadline: .now() + .seconds(seconds)) { CFRunLoopStop(loop) }
    }

    CFRunLoopRun()
    stopping = true
    inputObserver?.stop()
    if let failure { throw failure }
    try inputObserver?.flush()
    completed = true
    try database.stop(at: Date())
}
