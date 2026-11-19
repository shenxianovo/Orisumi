import AppKit

// A running application as the applications channel sees it; NSRunningApplication in production.
protocol RunningApplication: AnyObject {
    /// Identity across reads of the running list.
    var key: AnyHashable { get }
    var pid: pid_t { get }
    var bundleID: String? { get }
    var name: String? { get }
    var launchedAt: Date? { get }
    var isTerminated: Bool { get }
    var policy: ApplicationActivationPolicy? { get }
    func watchPolicy(_ changed: @escaping (ApplicationActivationPolicy?) -> Void) -> Cancel
}

struct ApplicationsSource {
    let running: () -> [RunningApplication]
    let frontmost: () -> RunningApplication?
    let watchRunning: (@escaping () -> Void) -> Cancel
    let watchActivation: (@escaping (RunningApplication?) -> Void) -> Cancel
}

// Running applications, their activation policies and the foreground application.
final class ApplicationsChannel: CaptureChannel {
    private final class Application {
        let app: RunningApplication
        let metadata: ObservedApplication
        var instance: String?
        var policyWatch: Cancel?

        init(_ app: RunningApplication) {
            self.app = app
            metadata = ObservedApplication(bundleID: app.bundleID, name: app.name, pid: app.pid, launchedAt: app.launchedAt)
        }
    }

    private let source: ApplicationsSource
    private let events: ObservationEvents
    private let failed: (Error) -> Void
    private var frontmostChanged: [(RunningApplication?) -> Void] = []
    private var watches: [Cancel] = []
    private var applications: [AnyHashable: Application] = [:]
    private var generation = 0

    init(source: ApplicationsSource, events: ObservationEvents, failed: @escaping (Error) -> Void) {
        self.source = source
        self.events = events
        self.failed = failed
    }

    var frontmost: RunningApplication? { source.frontmost() }

    /// Called after each recorded foreground change while the channel listens.
    func onFrontmostChange(_ handler: @escaping (RunningApplication?) -> Void) {
        frontmostChanged.append(handler)
    }

    func frontmostInstance(pid: pid_t, at time: Date) throws -> String? {
        guard let app = frontmost, app.pid == pid else { return nil }
        return try observeForeground(app, at: time)
    }

    /// The process instance of a running application this channel has recorded, if it is still the same process.
    func observedInstance(pid: pid_t, bundleID: String?) -> String? {
        guard let entry = applications.values.first(where: { $0.app.pid == pid && $0.metadata.pid == pid }),
              !entry.app.isTerminated, bundleID == nil || bundleID == "" || bundleID == entry.app.bundleID
        else { return nil }
        return entry.instance
    }

    func start() throws { try resume() }

    func interrupt(reason: String) throws {
        stopListening()
        events.interruptUnverifiedProcesses()
        try events.appendChannel("applications", available: false, reason: reason)
    }

    func resume() throws {
        stopListening()
        let generation = self.generation
        watches.append(source.watchRunning { [weak self] in
            let observedAt = Date()
            guard let self else { return }
            let running = self.source.running()
            deliverOnMain(self.failed) { [weak self] in
                guard let self, self.generation == generation else { return }
                try self.record(running, at: observedAt, snapshot: false)
            }
        })
        watches.append(source.watchActivation { [weak self] app in
            let observedAt = Date()
            guard let self else { return }
            deliverOnMain(self.failed) { [weak self] in
                guard let self, self.generation == generation else { return }
                let instance = try self.observeForeground(app, at: observedAt)
                try self.events.append(.activated, at: observedAt, instance: instance)
                self.frontmostChanged.forEach { $0(app) }
            }
        })
        try record(source.running(), at: Date(), snapshot: true)
        try events.appendChannel("applications", available: true, reason: "snapshot_received")
        let at = Date()
        let instance = try observeForeground(frontmost, at: at)
        try events.append(.snapshot, at: at, instance: instance)
    }

    // KVO has missed activation policy changes, so current values are compared every second.
    func reconcile() throws {
        let at = Date()
        _ = try observeForeground(frontmost, at: at)
        for entry in applications.values {
            try observePolicy(entry.app.isTerminated ? nil : entry.app.policy, of: entry, at: at)
        }
    }

    func stop() throws { stopListening() }

    private func stopListening() {
        generation += 1
        watches.forEach { $0() }
        watches.removeAll()
        for entry in applications.values { entry.policyWatch?() }
        applications.removeAll()
    }

    private func record(_ running: [RunningApplication], at observedAt: Date, snapshot: Bool) throws {
        let foreground = frontmost
        var listed = running
        if let foreground, !listed.contains(where: { $0.key == foreground.key }) { listed.append(foreground) }
        let current = Dictionary(listed.filter { $0.pid > 0 }.map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
        for (key, entry) in applications {
            if let app = current[key], matches(app, entry) { continue }
            try retire(entry, at: observedAt)
        }
        for app in listed where current[app.key] === app && applications[app.key] == nil {
            let entry = try track(app, at: observedAt)
            let policy = app.isTerminated ? nil : app.policy
            if app.key == foreground?.key || policy == .regular || events.hasVerifiedProcess(entry.metadata) {
                try qualify(entry, policy: policy, at: observedAt, snapshot: snapshot)
            }
        }
    }

    private func matches(_ app: RunningApplication, _ entry: Application) -> Bool {
        guard app.pid == entry.metadata.pid else { return false }
        if let previous = entry.metadata.launchedAt, let current = app.launchedAt { return previous == current }
        return true
    }

    private func retire(_ entry: Application, at time: Date) throws {
        entry.policyWatch?()
        if let instance = entry.instance {
            try events.appendApplicationLifecycle(.disappeared, at: time, app: entry.metadata, instance: instance)
        }
        applications.removeValue(forKey: entry.app.key)
    }

    private func track(_ app: RunningApplication, at time: Date) throws -> Application {
        if let existing = applications[app.key], matches(app, existing) { return existing }
        if let previous = applications.values.first(where: { $0.metadata.pid == app.pid || $0.app.key == app.key }) {
            try retire(previous, at: time)
        }
        let entry = Application(app)
        applications[app.key] = entry
        let generation = self.generation
        entry.policyWatch = app.watchPolicy { [weak self, weak entry] policy in
            let observedAt = Date()
            guard let self else { return }
            deliverOnMain(self.failed) { [weak self, weak entry] in
                guard let self, let entry, self.generation == generation,
                      self.applications[entry.app.key] === entry else { return }
                try self.observePolicy(policy, of: entry, at: observedAt)
            }
        }
        return entry
    }

    private func observeForeground(_ app: RunningApplication?, at time: Date) throws -> String? {
        guard let app, app.pid > 0 else { return nil }
        let entry = try track(app, at: time)
        try qualify(entry, policy: app.isTerminated ? nil : app.policy, at: time)
        return entry.instance
    }

    private func observePolicy(_ policy: ApplicationActivationPolicy?, of entry: Application, at time: Date) throws {
        if let instance = entry.instance {
            try events.appendActivationPolicy(policy, instance: instance, at: time)
        } else if policy == .regular {
            try qualify(entry, policy: policy, at: time)
        }
    }

    private func qualify(_ entry: Application, policy: ApplicationActivationPolicy?, at time: Date, snapshot: Bool = false) throws {
        guard entry.instance == nil else { return }
        entry.instance = try events.appendApplicationLifecycle(snapshot ? .snapshot : .appeared,
            at: time, app: entry.metadata, policy: policy)
    }
}
