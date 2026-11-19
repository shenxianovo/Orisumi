import Foundation

struct MediaObjectSource {
    typealias Cancel = () -> Void

    let list: () -> [UInt32]?
    let watchList: (@escaping () -> Void) -> Cancel?
    let watchRestart: ((@escaping () -> Void) -> Cancel?)?
    let identity: (UInt32) -> MediaObjectIdentity
    let matchApplication: ((UInt32, pid_t, String?) -> String?)?
    let read: (UInt32, MediaChannel) -> Bool?
    let watch: (UInt32, MediaChannel, @escaping () -> Void) -> Cancel?
}

final class MediaObjectObserver {
    private final class Object {
        var instance: String?
        var values: [MediaChannel: Bool?] = [:]
        var listeners: [MediaChannel: MediaObjectSource.Cancel] = [:]
        var identity: MediaObjectIdentity?
        var application: String?

        func includes(_ channel: MediaChannel) -> Bool {
            channel != .audioOutput || application != nil
        }

        deinit { listeners.values.forEach { $0() } }
    }

    private let source: MediaObjectSource
    private let channels: [MediaChannel]
    private let kind: MediaObjectKind
    private let events: ObservationEvents
    private let failed: (Error) -> Void
    private var objects: [UInt32: Object] = [:]
    private var listListener: MediaObjectSource.Cancel?
    private var restartListener: MediaObjectSource.Cancel?
    private var generation = 0
    private var hasSnapshot = false
    private var unavailable: Set<MediaChannel> = []

    init(source: MediaObjectSource, channels: [MediaChannel], kind: MediaObjectKind,
         events: ObservationEvents, failed: @escaping (Error) -> Void) {
        self.source = source
        self.channels = channels
        self.kind = kind
        self.events = events
        self.failed = failed
    }

    func stop() {
        generation += 1
        listListener?()
        listListener = nil
        restartListener?()
        restartListener = nil
        objects.removeAll()
        hasSnapshot = false
        unavailable.removeAll()
    }

    func interrupt(reason: String) throws {
        stop()
        if kind == .cameraDevice { try events.resetCameras() }
        for channel in channels {
            try events.appendChannel(channel.rawValue, available: false, reason: reason)
        }
    }

    func reconcile() throws {
        let generation = self.generation
        if restartListener == nil, let watchRestart = source.watchRestart {
            restartListener = watchRestart { [weak self] in
                guard let self, self.generation == generation else { return }
                self.record {
                    try self.interrupt(reason: "service_restarted")
                    try self.reconcile()
                }
            }
        }
        if listListener == nil {
            listListener = source.watchList { [weak self] in
                guard let self, self.generation == generation else { return }
                self.record { try self.refreshList() }
            }
        }
        try refreshList()
    }

    private func refreshList() throws {
        guard listListener != nil else {
            try interrupt(reason: "listener_unavailable")
            return
        }
        guard let listed = source.list() else {
            try interrupt(reason: "read_failed")
            return
        }
        let observedAt = Date()
        let current = Set(listed)
        for (id, object) in objects where !current.contains(id) {
            if let instance = object.instance {
                // Removed HAL clients no longer have any active streams.
                let stopped = kind == .audioProcess
                    ? channels.filter { object.includes($0) && object.values[$0] != .some(false) }
                    : []
                try events.appendMediaDisappearance(kind, instance: instance, objectID: id, stopped: stopped, at: observedAt)
            }
            objects.removeValue(forKey: id)
        }
        for id in listed where objects[id] == nil { objects[id] = Object() }

        for (id, object) in objects {
            try identify(id, object: object)
            for channel in channels where object.includes(channel) && object.listeners[channel] == nil {
                object.listeners[channel] = source.watch(id, channel) { [weak self, weak object] in
                    guard let self, let object, self.objects[id] === object else { return }
                    self.record { try self.sample(id, object: object, channel: channel) }
                }
            }
        }
        let missing = Set(channels.filter { channel in
            objects.values.contains { $0.includes(channel) && $0.listeners[channel] == nil }
        })
        let recovered = unavailable.subtracting(missing)

        for (id, object) in objects {
            let selected = channels.filter(object.includes)
            if selected.isEmpty { continue }
            if let identity = object.identity, object.instance == nil {
                for channel in selected {
                    let value = source.read(id, channel)
                    if value == nil { try reportReadFailure(id, channel: channel, at: Date()) }
                    object.values[channel] = .some(value)
                }
                object.instance = try events.appendMediaObject(kind, objectID: id, identity: identity, application: object.application,
                    event: hasSnapshot ? .appeared : .snapshot, values: object.values, at: Date())
            } else {
                for channel in selected {
                    if recovered.contains(channel) { object.values.removeValue(forKey: channel) }
                    // Check current values on reconciliation to catch missed notifications and retry failed reads.
                    try sample(id, object: object, channel: channel)
                }
            }
        }
        unavailable = missing
        for channel in channels {
            try events.appendChannel(channel.rawValue, available: !missing.contains(channel),
                reason: missing.contains(channel) ? "listener_unavailable" : "snapshot_received")
        }
        hasSnapshot = true
    }

    private func identify(_ id: UInt32, object: Object) throws {
        switch object.identity {
        case nil, .audio(nil, _): object.identity = source.identity(id)
        default: break
        }
        guard object.application == nil, case let .audio(pid?, bundle) = object.identity, pid > 0,
              let application = source.matchApplication?(id, pid, bundle) else { return }
        if let instance = object.instance { try events.linkAudioProcess(instance: instance, application: application) }
        object.application = application
    }

    private func reportReadFailure(_ id: UInt32, channel: MediaChannel, at time: Date) throws {
        try events.appendReadFailure("\(channel.rawValue): could not read object \(id)", at: time)
    }

    private func sample(_ id: UInt32, object: Object, channel: MediaChannel) throws {
        guard let instance = object.instance else { return }
        let value = source.read(id, channel)
        guard object.values[channel] != .some(value) else { return }
        let observedAt = Date()
        if value == nil { try reportReadFailure(id, channel: channel, at: observedAt) }
        try events.appendMediaState(value, channel: channel, instance: instance, at: observedAt)
        object.values[channel] = .some(value)
    }

    private func record(_ observation: () throws -> Void) {
        do { try observation() }
        catch {
            stop()
            failed(error)
        }
    }
}

// Audio processes or cameras as one channel: reconciling lists the objects and records each change.
extension MediaObjectObserver: CaptureChannel {
    func start() throws { try reconcile() }
    func resume() throws { try reconcile() }
}
