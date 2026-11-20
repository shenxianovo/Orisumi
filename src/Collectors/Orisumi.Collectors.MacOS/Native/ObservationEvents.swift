import Foundation

enum EventKind: String {
    case snapshot, activated, sleep, wake
    case sessionActive = "session_active"
    case sessionInactive = "session_inactive"
}

enum AFKEventKind: String {
    case snapshot, transition
}

enum AFKState: String {
    case active, idle
}

enum CapturePermission: String {
    case accessibility
    case inputMonitoring = "input_monitoring"
}

struct CapturePermissionState {
    private var previous: Bool?
    var granted: Bool { previous ?? false }

    mutating func observe(_ granted: Bool, record: (String) throws -> Void) rethrows {
        guard granted != previous else { return }
        try record(previous == nil ? (granted ? "snapshot_granted" : "snapshot_denied") : (granted ? "restored" : "lost"))
        previous = granted
    }
}

enum WindowEventKind: String {
    case snapshot
    case focusChanged = "focus_changed"
    case titleChanged = "title_changed"
    case readFailed = "read_failed"
}

enum AppLifecycleEventKind: String {
    case snapshot, appeared, disappeared
}

enum ApplicationActivationPolicy: String {
    case regular, accessory, prohibited
}

enum MediaObjectKind: String {
    case audioProcess = "audio_process"
    case cameraDevice = "camera_device"
}

enum MediaChannel: String, CaseIterable {
    case microphone, audioOutput = "audio_output", camera

    var kind: MediaObjectKind { self == .camera ? .cameraDevice : .audioProcess }
}

enum MediaObjectIdentity {
    case audio(pid: pid_t?, bundleID: String?)
    case camera(uid: String?, name: String?)
}

struct ObservedApplication {
    let bundleID: String?
    let name: String?
    let pid: Int32
    let launchedAt: Date?
}

func utcMilliseconds(_ time: Date) -> Int64 {
    Int64(time.timeIntervalSince1970 * 1000)
}

private func nullable<T>(_ value: T?) -> Any {
    value.map { $0 as Any } ?? NSNull()
}

// Handles and observation order are local to this stream. The receiver owns events identities.
final class ObservationEvents {
    private let send: (Data) throws -> Void
    private var pending = Data()
    private var nextInput: Int64 = 0
    private var nextHandle: Int64 = 0
    private var processes: [pid_t: (handle: String, launch: Date?)] = [:]
    private var activationPolicies: [String: ApplicationActivationPolicy?] = [:]
    private var channels: [String: (available: Bool, reason: String)] = [:]
    private var methods: [String: String] = [:]

    init(send: @escaping (Data) throws -> Void) {
        self.send = send
    }

    // Each newline terminates one transaction. Empty rounds carry the observer's progress too.
    func flush(at time: Date = Date(), stopped: Bool = false) throws {
        var batch = Data("{\"observedThrough\":\(utcMilliseconds(time)),\"stopped\":\(stopped),\"events\":[".utf8)
        batch.append(pending)
        batch.append(contentsOf: "]}\n".utf8)
        try send(batch)
        pending.removeAll(keepingCapacity: true)
    }

    private func emit(_ type: String, at time: Date, input: Int64? = nil, _ fields: [String: Any]) throws {
        let event = try JSONSerialization.data(withJSONObject: fields.merging([
            "type": type, "at": utcMilliseconds(time), "input": nullable(input),
        ]) { _, header in header })
        guard pending.count + event.count < ObservationPipe.capacity else { throw ObservationPipe.Failure.backpressure }
        if !pending.isEmpty { pending.append(44) }
        pending.append(event)
    }

    private func handle() -> String {
        nextHandle += 1
        return String(nextHandle)
    }

    func hasVerifiedProcess(_ app: ObservedApplication) -> Bool {
        app.launchedAt != nil && processes[app.pid]?.launch == app.launchedAt
    }

    private func process(_ app: ObservedApplication, at time: Date) throws -> String {
        if let known = processes[app.pid], known.launch == app.launchedAt { return known.handle }
        let instance = handle()
        try emit("process", at: time, [
            "instance": instance, "pid": app.pid, "bundle": nullable(app.bundleID),
            "name": nullable(app.name), "launched": nullable(app.launchedAt.map(utcMilliseconds)),
        ])
        processes[app.pid] = (instance, app.launchedAt)
        return instance
    }

    func interruptUnverifiedProcesses() {
        processes = processes.filter { $0.value.launch != nil }
        activationPolicies.removeAll()
    }

    func append(_ kind: EventKind, at time: Date, instance: String? = nil) throws {
        try emit("workspace", at: time, ["kind": kind.rawValue, "instance": nullable(instance)])
    }

    func appendScreenState(_ state: ScreenState, value: Bool?, snapshot: Bool, at time: Date) throws {
        try emit(state.rawValue, at: time, ["kind": snapshot ? "snapshot" : "transition", "value": nullable(value)])
    }

    func appendClock(_ kind: String, at time: Date, monotonicNS: Int64) throws {
        try emit("clock", at: time, ["kind": kind, "monotonic": monotonicNS])
    }

    func appendPermission(_ permission: CapturePermission, kind: String, at time: Date) throws {
        try emit("permission", at: time, ["permission": permission.rawValue, "kind": kind])
    }

    func appendTimeZone(_ identifier: String, at time: Date) throws {
        try emit("time_zone", at: time, ["zone": identifier])
    }

    func appendAFK(_ kind: AFKEventKind, at time: Date, state: AFKState, idleMS: Int64) throws {
        try emit("afk", at: time, ["kind": kind.rawValue, "state": state.rawValue, "idle": idleMS])
    }

    func appendChannel(_ channel: String, available: Bool, reason: String, readFailureDetail: String? = nil,
                       at time: Date = Date()) throws {
        if reason == "read_failed", channels[channel]?.reason != reason {
            let detail = readFailureDetail.map { " \($0)" } ?? ""
            try appendReadFailure("\(channel): \(reason)\(detail)", at: time)
        }
        defer { channels[channel] = (available, reason) }
        guard channels[channel]?.available != available else { return }
        try emit("channel", at: time, ["channel": channel, "available": available, "reason": reason])
    }

    func appendReadFailure(_ error: String, at time: Date) throws {
        try emit("read_failure", at: time, ["error": error])
    }

    func appendMethod(_ channel: String, mode: String, at time: Date = Date()) throws {
        guard methods[channel] != mode else { return }
        try emit("method", at: time, ["channel": channel, "mode": mode])
        methods[channel] = mode
    }

    func appendWindow(_ kind: WindowEventKind, at time: Date, instance: String? = nil, title: String?, hasWindow: Bool?) throws {
        try emit("window", at: time, ["kind": kind.rawValue, "instance": nullable(instance),
                                      "hasWindow": nullable(hasWindow), "title": nullable(title)])
    }

    @discardableResult
    func appendApplicationLifecycle(_ kind: AppLifecycleEventKind, at time: Date, app: ObservedApplication,
                                    policy: ApplicationActivationPolicy? = nil, instance existing: String? = nil) throws -> String {
        let instance = try existing ?? process(app, at: time)
        try emit("application", at: time, ["kind": kind.rawValue, "instance": instance, "policy": nullable(policy?.rawValue)])
        if kind == .disappeared {
            if processes[app.pid]?.handle == instance { processes.removeValue(forKey: app.pid) }
            activationPolicies.removeValue(forKey: instance)
        } else {
            activationPolicies[instance] = .some(policy)
        }
        return instance
    }

    func appendActivationPolicy(_ policy: ApplicationActivationPolicy?, instance: String, at time: Date) throws {
        guard activationPolicies[instance] != .some(policy) else { return }
        try emit("policy", at: time, ["instance": instance, "policy": nullable(policy?.rawValue)])
        activationPolicies[instance] = .some(policy)
    }

    func resetCameras() throws { try emit("camera_reset", at: Date(), [:]) }

    func appendMediaObject(_ kind: MediaObjectKind, objectID: UInt32, identity: MediaObjectIdentity, application: String?,
                           event: AppLifecycleEventKind, values: [MediaChannel: Bool?], at time: Date) throws -> String {
        let instance = handle()
        var fields: [String: Any] = [
            "kind": event.rawValue, "instance": instance, "object": objectID, "media": kind.rawValue,
            "values": Dictionary(uniqueKeysWithValues: values.map { ($0.key.rawValue, nullable($0.value)) }),
        ]
        switch identity {
        case let .audio(pid, bundle):
            fields["pid"] = nullable(pid)
            fields["bundle"] = nullable(bundle)
            fields["application"] = nullable(application)
        case let .camera(uid, name):
            fields["uid"] = nullable(uid)
            fields["name"] = nullable(name)
        }
        try emit("media_presence", at: time, fields)
        return instance
    }

    func appendMediaDisappearance(_ kind: MediaObjectKind, instance: String, objectID: UInt32,
                                  stopped: [MediaChannel], at time: Date) throws {
        try emit("media_presence", at: time, [
            "kind": "disappeared", "instance": instance, "object": objectID, "media": kind.rawValue,
            "values": Dictionary(uniqueKeysWithValues: stopped.map { ($0.rawValue, false) }),
        ])
    }

    func appendMediaState(_ value: Bool?, channel: MediaChannel, instance: String, at time: Date) throws {
        try emit("media_state", at: time, ["channel": channel.rawValue, "instance": instance, "value": nullable(value)])
    }

    func linkAudioProcess(instance: String, application: String) throws {
        try emit("audio_link", at: Date(), ["instance": instance, "application": application])
    }

    func reserveInput(at time: Date) throws -> Int64 {
        nextInput += 1
        try emit("input_start", at: time, input: nextInput, [:])
        return nextInput
    }

    func appendInputBatch(_ observations: [InputObservation]) throws {
        for observation in observations {
            switch observation.event {
            case let .keyboardPress(press):
                try emit("keyboard", at: observation.observedAt, input: observation.token, [
                    "released": nullable(press.releasedAt.map(utcMilliseconds)), "key": press.keyCode, "modifiers": press.modifiersAtPress,
                ])
            case let .mouse(kind, x, y, button):
                try emit("mouse", at: observation.observedAt, input: observation.token, [
                    "kind": kind, "x": x, "y": y, "button": button,
                ])
            }
        }
    }
}
