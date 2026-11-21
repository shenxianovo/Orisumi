import AppKit

// The session loop: records sleep and session switches, and tells every channel when the observation chain
// starts, breaks, resumes and stops. What each channel records is up to the channel.
func captureForeground(in events: ObservationEvents, seconds: Int?, captureWindows: Bool,
                       afkThresholdMS: Int64, captureInput: Bool, mediaChannels: Set<MediaChannel>) throws {
    let loop = CFRunLoopGetMain()!
    // EOF means the receiver is gone, including while the stdout worker is blocked.
    DispatchQueue.global().async {
        while let command = readLine() {
            if command == "stop" { DispatchQueue.main.async { CFRunLoopStop(loop) } }
        }
        _exit(1)
    }
    let workspace = NSWorkspace.shared
    var failure: Error?
    var stopping = false
    func fail(_ error: Error) {
        guard !stopping else { return }
        failure = error
        stopping = true
        CFRunLoopStop(loop)
    }

    let applications = ApplicationsChannel(source: .workspace(workspace), events: events, failed: fail)
    var channels: [CaptureChannel] = [applications]
    if captureWindows {
        channels.append(WindowsChannel(source: .accessibility, applications: applications, events: events, failed: fail))
    }
    channels.append(InputChannel(source: .session, captureInput: captureInput, afkThresholdMS: afkThresholdMS,
                                 events: events, failed: fail))
    for kind in [MediaObjectKind.audioProcess, .cameraDevice] {
        let selected = MediaChannel.allCases.filter { $0.kind == kind && mediaChannels.contains($0) }
        guard !selected.isEmpty else { continue }
        let source = kind == .audioProcess ? MediaHardware.audio(applications: applications) : MediaHardware.camera()
        channels.append(MediaObjectObserver(source: source, channels: selected, kind: kind, events: events, failed: fail))
    }
    channels.append(TimeZoneChannel(read: SystemTime.timeZone, watch: SystemTime.notification(.NSSystemTimeZoneDidChange),
                                    events: events, failed: fail))
    channels.append(ClockChannel(read: SystemTime.clock, watch: SystemTime.notification(.NSSystemClockDidChange),
                                 events: events, failed: fail))

    let screenStates = [
        ScreenStateObserver(.lock, source: .lock(), events: events, failed: fail),
        ScreenStateObserver(.displaySleep, source: .displaySleep(workspace), events: events, failed: fail),
    ]
    defer { screenStates.forEach { $0.stop() } }
    for observer in screenStates { try observer.start() }

    var asleep = false
    var sessionInactive = false
    var paused: Bool { asleep || sessionInactive }
    let subscriptions: [(Notification.Name, EventKind)] = [
        (NSWorkspace.willSleepNotification, .sleep),
        (NSWorkspace.didWakeNotification, .wake),
        (NSWorkspace.sessionDidBecomeActiveNotification, .sessionActive),
        (NSWorkspace.sessionDidResignActiveNotification, .sessionInactive),
    ]
    let observers = subscriptions.map { name, kind in
        workspace.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { _ in
            guard !stopping else { return }
            let wasPaused = paused
            do {
                try events.append(kind, at: Date())
                switch kind {
                case .sleep: asleep = true
                case .wake: asleep = false
                case .sessionInactive: sessionInactive = true
                case .sessionActive: sessionInactive = false
                case .activated, .snapshot: break
                }
                if paused && !wasPaused {
                    for channel in channels { try channel.interrupt(reason: "session_paused") }
                } else if !paused && wasPaused {
                    for observer in screenStates { try observer.snapshot() }
                    for channel in channels { try channel.resume() }
                }
            } catch {
                fail(error)
            }
        }
    }
    defer { observers.forEach(workspace.notificationCenter.removeObserver) }

    var started: [CaptureChannel] = []
    defer {
        for channel in started.reversed() {
            do { try channel.stop() }
            catch { fputs("orisumi: could not stop a capture channel: \(error.localizedDescription)\n", stderr) }
        }
    }
    for channel in channels {
        try channel.start()
        started.append(channel)
    }

    try events.flush()

    let refresh = Timer(timeInterval: 1, repeats: true) { _ in
        guard !stopping else { return }
        do {
            if !paused {
                for channel in channels { try channel.reconcile() }
            }
            try events.flush()
        } catch {
            fail(error)
        }
    }
    RunLoop.main.add(refresh, forMode: .common)
    defer { refresh.invalidate() }

    if let seconds {
        DispatchQueue.main.asyncAfter(deadline: .now() + .seconds(seconds)) { CFRunLoopStop(loop) }
    }

    if !stopping { CFRunLoopRun() }
    stopping = true
    if let failure { throw failure }
    let stopped = started
    started.removeAll()
    for channel in stopped.reversed() { try channel.stop() }
    try events.flush(stopped: true)
}
