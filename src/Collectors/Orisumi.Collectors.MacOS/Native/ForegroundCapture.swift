import AppKit
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
    var afkState: AFKState?
    var inputObserver: InputEventObserver?
    var lastTimeZoneID: String?
    func sampleTimeZone(at observedAt: Date) throws {
        let identifier = TimeZone.autoupdatingCurrent.identifier
        guard identifier != lastTimeZoneID else { return }
        try database.appendTimeZone(identifier, at: observedAt)
        lastTimeZoneID = identifier
    }
    // CoreGraphics defines kCGAnyInputEventType as UInt32.max, which Swift does not import.
    let anyInput = CGEventType(rawValue: UInt32.max)!
    func sampleAFK() throws {
        guard !asleep && !sessionInactive else { return }
        let observedAt = Date()
        let idleMilliseconds = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput) * 1000
        guard idleMilliseconds >= 0 && idleMilliseconds < Double(Int64.max) else { return }
        let idleMS = Int64(idleMilliseconds)
        let state: AFKState = idleMS >= afkThresholdMS ? .idle : .active
        guard state != afkState else { return }
        try database.appendAFK(afkState == nil ? .snapshot : .transition, at: observedAt, state: state, idleMS: idleMS)
        afkState = state
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
                    afkState = nil
                case .sessionInactive:
                    sessionInactive = true
                    afkState = nil
                case .wake:
                    asleep = false
                    try sampleAFK()
                case .sessionActive:
                    sessionInactive = false
                    try sampleAFK()
                case .activated, .snapshot: break
                }
                if kind == .sleep || kind == .sessionInactive || kind == .wake || kind == .sessionActive {
                    try inputObserver?.setPaused(asleep || sessionInactive)
                }
                if let windowObserver {
                    switch kind {
                    case .sleep, .sessionInactive:
                        windowObserver.detach()
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
                                    guard !stopping && !asleep && !sessionInactive,
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
    let lifecycleObserver = ApplicationLifecycleObserver(workspace: workspace, database: database, failed: fail)
    defer { lifecycleObserver.stop() }
    if let failure { throw failure }
    try database.append(.snapshot, at: Date(), app: workspace.frontmostApplication.map(ForegroundApplication.init))
    try windowObserver?.watch(workspace.frontmostApplication, kind: .snapshot)
    try sampleAFK()
    if captureInput {
        inputObserver = try InputEventObserver(database: database, failed: fail)
    }
    let refresh = Timer(timeInterval: 1, repeats: true) { _ in
        guard !stopping && !asleep && !sessionInactive else { return }
        do {
            try sampleAFK()
            try windowObserver?.watch(workspace.frontmostApplication, kind: .focusChanged)
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
    try database.stop(at: Date())
}
