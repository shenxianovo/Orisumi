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
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var frontmostChanged: [(RunningApplication?) -> Void] = []
    private var watches: [Cancel] = []
    private var applications: [AnyHashable: Application] = [:]
    private var generation = 0

    init(source: ApplicationsSource, database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.source = source
        self.database = database
        self.failed = failed
    }

    var frontmost: RunningApplication? { source.frontmost() }

    /// Called after each recorded foreground change while the channel listens.
    func onFrontmostChange(_ handler: @escaping (RunningApplication?) -> Void) {
        frontmostChanged.append(handler)
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
        database.interruptUnverifiedProcesses()
        try database.appendChannel("applications", available: false, reason: reason)
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
        try record(source.running(), at: Date(), snapshot: true)
        try database.appendChannel("applications", available: true, reason: "snapshot_received")
        watches.append(source.watchActivation { [weak self] app in
            guard let self else { return }
            deliverOnMain(self.failed) { [weak self] in
                guard let self, self.generation == generation else { return }
                try self.database.append(.activated, at: Date(), app: app.map(ForegroundApplication.init))
                self.frontmostChanged.forEach { $0(app) }
            }
        })
        try database.append(.snapshot, at: Date(), app: source.frontmost().map(ForegroundApplication.init))
    }

    // KVO has missed activation policy changes, so current values are compared every second.
    func reconcile() throws {
        let observedAt = Date()
        for entry in applications.values {
            guard let instance = entry.instance else { continue }
            try database.appendActivationPolicy(entry.app.isTerminated ? nil : entry.app.policy, instance: instance, at: observedAt)
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
        let current = Dictionary(running.filter { $0.pid > 0 }.map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
        for (key, entry) in applications where current[key] == nil || current[key]!.pid != entry.metadata.pid {
            entry.policyWatch?()
            try database.appendApplicationLifecycle(.disappeared, at: observedAt, app: entry.metadata, instance: entry.instance)
            applications.removeValue(forKey: key)
        }
        let generation = self.generation
        for app in running where current[app.key] === app && applications[app.key] == nil {
            let entry = Application(app)
            applications[app.key] = entry
            entry.policyWatch = app.watchPolicy { [weak self, weak entry] policy in
                let observedAt = Date()
                guard let self else { return }
                deliverOnMain(self.failed) { [weak self, weak entry] in
                    guard let self, let entry, self.generation == generation,
                          self.applications[entry.app.key] === entry, let instance = entry.instance else { return }
                    try self.database.appendActivationPolicy(policy, instance: instance, at: observedAt)
                }
            }
            entry.instance = try database.appendApplicationLifecycle(snapshot ? .snapshot : .appeared,
                at: observedAt, app: entry.metadata, policy: app.isTerminated ? nil : app.policy)
        }
    }
}

extension ForegroundApplication {
    init(_ app: RunningApplication) {
        self.init(bundleID: app.bundleID, name: app.name, pid: app.pid)
    }
}
