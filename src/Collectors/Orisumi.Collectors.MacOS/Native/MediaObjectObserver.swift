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
        var unlinkedAudio: (pid: pid_t, bundleID: String?)?

        deinit { listeners.values.forEach { $0() } }
    }

    private let source: MediaObjectSource
    private let channels: [MediaChannel]
    private let kind: MediaObjectKind
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var objects: [UInt32: Object] = [:]
    private var listListener: MediaObjectSource.Cancel?
    private var restartListener: MediaObjectSource.Cancel?
    private var generation = 0
    private var hasSnapshot = false
    private var unavailable: Set<MediaChannel> = []

    init(source: MediaObjectSource, channels: [MediaChannel], kind: MediaObjectKind,
         database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.source = source
        self.channels = channels
        self.kind = kind
        self.database = database
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
        for channel in channels {
            try database.appendChannel(channel.rawValue, available: false, reason: reason)
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
            // A client removed from the HAL list no longer has any active HAL streams.
            let stopped = kind == .audioProcess ? channels.filter { object.values[$0] != .some(false) } : []
            try database.appendMediaDisappearance(kind, instance: object.instance!, objectID: id, stopped: stopped, at: observedAt)
            objects.removeValue(forKey: id)
        }
        for id in listed where objects[id] == nil { objects[id] = Object() }

        for (id, object) in objects {
            for channel in channels where object.listeners[channel] == nil {
                object.listeners[channel] = source.watch(id, channel) { [weak self, weak object] in
                    guard let self, let object, self.objects[id] === object else { return }
                    self.record {
                        try self.sample(id, object: object, channel: channel)
                        try self.linkApplication(id, object: object)
                    }
                }
            }
        }
        let missing = Set(channels.filter { channel in objects.values.contains { $0.listeners[channel] == nil } })
        let recovered = unavailable.subtracting(missing)

        for (id, object) in objects {
            if object.instance == nil {
                for channel in channels { object.values[channel] = .some(source.read(id, channel)) }
                let identity = source.identity(id)
                object.instance = try database.appendMediaObject(kind, objectID: id, identity: identity,
                    event: hasSnapshot ? .appeared : .snapshot, values: object.values, at: Date())
                if case let .audio(pid?, bundleID, nil) = identity, pid > 0 {
                    object.unlinkedAudio = (pid, bundleID)
                }
            } else {
                for channel in channels {
                    if recovered.contains(channel) { object.values.removeValue(forKey: channel) }
                    // Check current values on reconciliation to catch missed notifications and retry failed reads.
                    try sample(id, object: object, channel: channel)
                }
                try linkApplication(id, object: object)
            }
        }
        unavailable = missing
        for channel in channels {
            try database.appendChannel(channel.rawValue, available: !missing.contains(channel),
                reason: missing.contains(channel) ? "listener_unavailable" : "snapshot_received")
        }
        hasSnapshot = true
    }

    private func linkApplication(_ id: UInt32, object: Object) throws {
        guard let instance = object.instance, let candidate = object.unlinkedAudio,
              let application = source.matchApplication?(id, candidate.pid, candidate.bundleID) else { return }
        try database.linkAudioProcess(instance: instance, application: application)
        object.unlinkedAudio = nil
    }

    private func sample(_ id: UInt32, object: Object, channel: MediaChannel) throws {
        guard let instance = object.instance else { return }
        let value = source.read(id, channel)
        guard object.values[channel] != .some(value) else { return }
        try database.appendMediaState(value, channel: channel, instance: instance, at: Date())
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
