import AppKit

// The session loop: records sleep and session switches, and tells every channel when the observation chain
// starts, breaks, resumes and stops. What each channel records is up to the channel.
func captureForeground(in database: ForegroundDatabase, seconds: Int?, captureWindows: Bool,
                       afkThresholdMS: Int64, captureInput: Bool, mediaChannels: Set<MediaChannel>) throws {
    let workspace = NSWorkspace.shared
    let loop = CFRunLoopGetMain()!
    var failure: Error?
    var stopping = false
    func fail(_ error: Error) {
        guard !stopping else { return }
        failure = error
        stopping = true
        CFRunLoopStop(loop)
    }

    let applications = ApplicationsChannel(source: .workspace(workspace), database: database, failed: fail)
    var channels: [CaptureChannel] = [applications]
    if captureWindows {
        channels.append(WindowsChannel(source: .accessibility, applications: applications, database: database, failed: fail))
    }
    channels.append(InputChannel(source: .session, captureInput: captureInput, afkThresholdMS: afkThresholdMS,
                                 database: database, failed: fail))
    for kind in [MediaObjectKind.audioProcess, .cameraDevice] {
        let selected = MediaChannel.allCases.filter { $0.kind == kind && mediaChannels.contains($0) }
        guard !selected.isEmpty else { continue }
        let source = kind == .audioProcess ? MediaHardware.audio(applications: applications) : MediaHardware.camera()
        channels.append(MediaObjectObserver(source: source, channels: selected, kind: kind, database: database, failed: fail))
    }
    channels.append(TimeZoneChannel(read: SystemTime.timeZone, watch: SystemTime.notification(.NSSystemTimeZoneDidChange),
                                    database: database, failed: fail))
    channels.append(ClockChannel(read: SystemTime.clock, watch: SystemTime.notification(.NSSystemClockDidChange),
                                 database: database, failed: fail))

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
                try database.append(kind, at: Date())
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
                    for channel in channels { try channel.resume() }
                }
            } catch {
                fail(error)
            }
        }
    }
    defer { observers.forEach(workspace.notificationCenter.removeObserver) }

    try database.start(at: Date(), afkThresholdMS: afkThresholdMS)
    var completed = false
    defer {
        if !completed {
            do { try database.stop(at: Date(), failed: true) }
            catch { fputs("orisumi: could not record failed capture stop: \(error.localizedDescription)\n", stderr) }
        }
    }
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

    let refresh = Timer(timeInterval: 1, repeats: true) { _ in
        guard !stopping, !paused else { return }
        do {
            for channel in channels { try channel.reconcile() }
        } catch {
            fail(error)
        }
    }
    RunLoop.main.add(refresh, forMode: .common)
    defer { refresh.invalidate() }

    // The .NET parent closes stdin, or writes "stop", to request a clean stop.
    DispatchQueue.global().async {
        while let command = readLine(), command != "stop" {}
        DispatchQueue.main.async { CFRunLoopStop(loop) }
    }
    if let seconds {
        DispatchQueue.main.asyncAfter(deadline: .now() + .seconds(seconds)) { CFRunLoopStop(loop) }
    }

    CFRunLoopRun()
    stopping = true
    if let failure { throw failure }
    let stopped = started
    started.removeAll()
    for channel in stopped.reversed() { try channel.stop() }
    completed = true
    try database.stop(at: Date())
}
