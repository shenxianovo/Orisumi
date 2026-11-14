import AppKit

final class ApplicationLifecycleObserver {
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var observation: NSKeyValueObservation?
    private var applications: [NSRunningApplication: ObservedApplication] = [:]
    private var hasSnapshot = false
    private var observing = false

    init(workspace: NSWorkspace, database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.database = database
        self.failed = failed
        observing = true
        observation = workspace.observe(\.runningApplications, options: [.initial]) { [weak self] workspace, _ in
            let observedAt = Date()
            let running = workspace.runningApplications
            if Thread.isMainThread {
                self?.record(running, at: observedAt)
            } else {
                DispatchQueue.main.async { [weak self] in
                    self?.record(running, at: observedAt)
                }
            }
        }
    }

    func stop() {
        observing = false
        observation?.invalidate()
    }

    func pause() { observing = false }

    func resume(_ running: [NSRunningApplication]) {
        applications.removeAll()
        hasSnapshot = false
        observing = true
        record(running, at: Date())
    }

    private func record(_ running: [NSRunningApplication], at observedAt: Date) {
        guard observing else { return }
        var current: [NSRunningApplication: ObservedApplication] = [:]
        for app in running {
            let pid = app.processIdentifier
            guard pid > 0 else { continue }
            let known = applications[app]
            let previous = known?.pid == pid ? known : nil
            current[app] = ObservedApplication(
                bundleID: app.bundleIdentifier ?? previous?.bundleID,
                name: app.localizedName ?? previous?.name,
                pid: pid,
                launchedAt: app.launchDate ?? previous?.launchedAt
            )
        }

        do {
            for (app, metadata) in applications where current[app]?.pid != metadata.pid {
                try database.appendApplicationLifecycle(.disappeared, at: observedAt, app: metadata)
            }
            for (app, metadata) in current where applications[app]?.pid != metadata.pid {
                let kind: AppLifecycleEventKind = hasSnapshot ? .appeared : .snapshot
                try database.appendApplicationLifecycle(kind, at: observedAt, app: metadata)
            }
            applications = current
            hasSnapshot = true
        } catch {
            stop()
            failed(error)
        }
    }
}
