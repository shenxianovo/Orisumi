import AppKit

final class ApplicationLifecycleObserver {
    private final class Application {
        let metadata: ObservedApplication
        var instance: String?
        var policyObservation: NSKeyValueObservation?

        init(_ app: NSRunningApplication) {
            metadata = ObservedApplication(bundleID: app.bundleIdentifier, name: app.localizedName,
                                           pid: app.processIdentifier, launchedAt: app.launchDate)
        }
    }

    private let workspace: NSWorkspace
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var observation: NSKeyValueObservation?
    private var applications: [NSRunningApplication: Application] = [:]
    private var generation = 0

    init(workspace: NSWorkspace, database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.workspace = workspace
        self.database = database
        self.failed = failed
    }

    func pause() {
        generation += 1
        observation?.invalidate()
        observation = nil
        for entry in applications.values { entry.policyObservation?.invalidate() }
        applications.removeAll()
    }

    func resume() throws {
        pause()
        let generation = self.generation
        observation = workspace.observe(\.runningApplications) { [weak self] workspace, _ in
            let observedAt = Date()
            let running = workspace.runningApplications
            self?.deliver {
                guard let self, self.generation == generation else { return }
                try self.record(running, at: observedAt, snapshot: false)
            }
        }
        try record(workspace.runningApplications, at: Date(), snapshot: true)
    }

    func reconcilePolicies() throws {
        let observedAt = Date()
        for (app, entry) in applications {
            guard let instance = entry.instance else { continue }
            try database.appendActivationPolicy(currentPolicy(of: app), instance: instance, at: observedAt)
        }
    }

    func observedInstance(of app: NSRunningApplication) -> String? {
        guard let entry = applications[app], entry.metadata.pid == app.processIdentifier else { return nil }
        return entry.instance
    }

    private func currentPolicy(of app: NSRunningApplication) -> ApplicationActivationPolicy? {
        app.isTerminated ? nil : ApplicationActivationPolicy(app.activationPolicy)
    }

    private func record(_ running: [NSRunningApplication], at observedAt: Date, snapshot: Bool) throws {
        let current = Set(running.filter { $0.processIdentifier > 0 })
        for (app, entry) in applications where !current.contains(app) || app.processIdentifier != entry.metadata.pid {
            entry.policyObservation?.invalidate()
            try database.appendApplicationLifecycle(.disappeared, at: observedAt, app: entry.metadata, instance: entry.instance)
            applications.removeValue(forKey: app)
        }
        for app in running where current.contains(app) && applications[app] == nil {
            let entry = Application(app)
            applications[app] = entry
            entry.policyObservation = app.observe(\.activationPolicy, options: [.new]) { [weak self, weak entry] app, change in
                let observedAt = Date()
                let value = app.isTerminated ? nil : ApplicationActivationPolicy(change.newValue ?? app.activationPolicy)
                self?.deliver {
                    guard let self, let entry, self.applications[app] === entry, let instance = entry.instance else { return }
                    try self.database.appendActivationPolicy(value, instance: instance, at: observedAt)
                }
            }
            let policy = currentPolicy(of: app)
            entry.instance = try database.appendApplicationLifecycle(snapshot ? .snapshot : .appeared,
                at: observedAt, app: entry.metadata, policy: policy)
        }
    }

    private func deliver(_ record: @escaping () throws -> Void) {
        let work = { [weak self] in
            do { try record() }
            catch {
                self?.pause()
                self?.failed(error)
            }
        }
        if Thread.isMainThread { work() }
        else { DispatchQueue.main.async(execute: work) }
    }
}

private extension ApplicationActivationPolicy {
    init?(_ value: NSApplication.ActivationPolicy) {
        switch value {
        case .regular: self = .regular
        case .accessory: self = .accessory
        case .prohibited: self = .prohibited
        @unknown default: return nil
        }
    }
}
