import Foundation

// A system notification as a cancellable subscription.
typealias SystemNotification = (@escaping () -> Void) -> Cancel

// The time zone: initial value, then each change.
final class TimeZoneChannel: CaptureChannel {
    private let read: () -> String
    private let watch: SystemNotification
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var cancel: Cancel?
    private var last: String?

    init(read: @escaping () -> String, watch: @escaping SystemNotification, database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.read = read
        self.watch = watch
        self.database = database
        self.failed = failed
    }

    func start() throws { try resume() }

    func interrupt(reason: String) throws { try stop() }

    func resume() throws {
        try stop()
        cancel = watch { [weak self] in
            guard let self else { return }
            deliverOnMain(self.failed) { [weak self] in try self?.reconcile() }
        }
        try record(read())
    }

    func reconcile() throws {
        let identifier = read()
        if identifier != last { try record(identifier) }
    }

    func stop() throws {
        cancel?()
        cancel = nil
    }

    private func record(_ identifier: String) throws {
        try database.appendTimeZone(identifier, at: Date())
        last = identifier
    }
}

// Wall clock against the monotonic clock: their difference changes only when the clock is set.
final class ClockChannel: CaptureChannel {
    private let read: () -> (Date, Int64)
    private let watch: SystemNotification
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var cancel: Cancel?

    init(read: @escaping () -> (Date, Int64), watch: @escaping SystemNotification, database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.read = read
        self.watch = watch
        self.database = database
        self.failed = failed
    }

    func start() throws { try resume() }

    func interrupt(reason: String) throws { try stop() }

    func resume() throws {
        try stop()
        cancel = watch { [weak self] in
            guard let self else { return }
            deliverOnMain(self.failed) { [weak self] in try self?.record("changed") }
        }
        try record("snapshot")
    }

    func reconcile() throws {}

    func stop() throws {
        cancel?()
        cancel = nil
    }

    private func record(_ kind: String) throws {
        let (time, monotonicNS) = read()
        try database.appendClock(kind, at: time, monotonicNS: monotonicNS)
    }
}
