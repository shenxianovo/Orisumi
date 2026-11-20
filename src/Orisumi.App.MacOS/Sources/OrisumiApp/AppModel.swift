import Foundation
import Observation

@MainActor
@Observable
final class AppModel {
    private let cli: CLI?
    let cliPath: String?
    private(set) var status: CaptureStatus?
    private(set) var coverage: Coverage?
    private(set) var windowOpen = false
    private(set) var busy = false
    private(set) var statusError: String?
    private(set) var coverageError: String?
    var actionError: String?
    var windowRequested = false

    @ObservationIgnored private var windowVisible = false
    @ObservationIgnored private var statusTimer: Task<Void, Never>?
    @ObservationIgnored private var coverageTimer: Task<Void, Never>?
    @ObservationIgnored private var statusCall: Task<Void, Never>?
    @ObservationIgnored private var coverageCall: Task<Void, Never>?

    init(arguments: [String], bundle: URL) {
        do {
            let configuration = try CLIConfiguration(arguments: arguments, bundle: bundle)
            cli = CLI(configuration: configuration)
            cliPath = configuration.executable.path
        } catch {
            cli = nil
            cliPath = nil
            statusError = error.localizedDescription
        }
    }

    var health: Health { status?.health ?? .unavailable }
    var hasLoadedCollectors: Bool { status?.anyLoaded == true }
    var startTitle: String { hasLoadedCollectors ? "Restart Capture" : "Start Capture" }
    var mayStart: Bool { status?.collectors.isEmpty == false && !busy }
    var mayStop: Bool { hasLoadedCollectors && !busy }
    var queryError: String? { statusError ?? coverageError }

    func startPolling() {
        guard cli != nil else { return }
        statusTimer?.cancel()
        statusTimer = Task { [weak self, windowVisible] in
            let clock = ContinuousClock()
            let interval: Duration = windowVisible ? .seconds(5) : .seconds(30)
            while !Task.isCancelled {
                let next = clock.now + interval
                if let self, !self.busy { await self.refreshStatus() }
                try? await clock.sleep(until: next)
            }
        }
        coverageTimer?.cancel()
        if windowVisible {
            coverageTimer = Task { [weak self] in
                let clock = ContinuousClock()
                while !Task.isCancelled {
                    let next = clock.now + .seconds(60)
                    if let self, !self.busy { await self.refreshCoverage() }
                    try? await clock.sleep(until: next)
                }
            }
        }
    }

    func windowChanged(open: Bool, visible: Bool) {
        windowOpen = open
        guard windowVisible != visible else { return }
        windowVisible = visible
        startPolling()
    }

    func refresh() async {
        async let status: Void = refreshStatus()
        async let coverage: Void = refreshCoverage()
        _ = await (status, coverage)
    }

    func perform(_ action: CaptureAction) async {
        guard let cli, action == .start ? mayStart : mayStop else { return }
        busy = true
        actionError = nil
        defer { busy = false }
        do {
            try await cli.capture(action)
        } catch {
            actionError = error.localizedDescription
        }
        // A query already in flight could have read the state before the action.
        await statusCall?.value
        await coverageCall?.value
        await refresh()
    }

    private func refreshStatus() async {
        guard let cli else { return }
        if let statusCall { return await statusCall.value }
        let call = Task {
            defer { statusCall = nil }
            do {
                status = try await cli.status()
                statusError = nil
            } catch {
                status = nil
                statusError = error.localizedDescription
            }
        }
        statusCall = call
        await call.value
    }

    private func refreshCoverage() async {
        guard let cli else { return }
        if let coverageCall { return await coverageCall.value }
        let call = Task {
            defer { coverageCall = nil }
            let now = Date.now
            do {
                coverage = try await cli.coverage(from: Calendar.current.startOfDay(for: now), to: now)
                coverageError = nil
            } catch {
                coverage = nil
                coverageError = error.localizedDescription
            }
        }
        coverageCall = call
        await call.value
    }
}
