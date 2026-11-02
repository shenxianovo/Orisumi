import Foundation
import Testing
@testable import OrisumiClient

/// Drives a real Runtime with its bundled packages against a running API. Set ORISUMI_APP to a built Orisumi.app and
/// ORISUMI_BACKEND to the API address to run it, for example after `orisumi mac build` and `orisumi env up api --temp`.
@MainActor
@Suite(.serialized, .enabled(if: ProcessInfo.processInfo.environment["ORISUMI_APP"] != nil))
struct RuntimeIntegrationTests {
    static let packageId = "foreground-state"
    static let backend = URL(string: ProcessInfo.processInfo.environment["ORISUMI_BACKEND"] ?? "http://localhost:8080/api/")!

    static func model(dataDirectory: URL) -> RuntimeModel {
        let app = URL(filePath: ProcessInfo.processInfo.environment["ORISUMI_APP"]!, directoryHint: .isDirectory)
        let configuration = RuntimeConfiguration(
            executable: app.appending(path: "Contents/Helpers/Runtime/Orisumi.Observer.Runtime"),
            dataDirectory: dataDirectory,
            backend: backend,
            bundled: app.appending(path: "Contents/Resources/Observers", directoryHint: .isDirectory))
        return RuntimeModel(transport: RuntimeConnection { configuration })
    }

    static func waitFor(_ model: RuntimeModel, seconds: Double = 20,
                        _ condition: (ObserverStatus) -> Bool) async throws -> ObserverStatus {
        let deadline = Date.now.addingTimeInterval(seconds)
        while Date.now < deadline {
            await model.refresh()
            if let observer = model.observer(packageId), condition(observer) { return observer }
            try await Task.sleep(for: .milliseconds(200))
        }
        Issue.record("Timed out; last status: \(String(describing: model.observer(packageId)))")
        throw CancellationError()
    }

    static func waitUntil(seconds: Double = 30, _ condition: () async throws -> Bool) async throws {
        let deadline = Date.now.addingTimeInterval(seconds)
        while Date.now < deadline {
            if try await condition() { return }
            try await Task.sleep(for: .milliseconds(200))
        }
        Issue.record("Timed out")
        throw CancellationError()
    }

    /// Whether anything the instance wrote has reached the API.
    static func hasSaved(_ instance: UUID) async throws -> Bool {
        let url = URL(string: "observations?observerId=\(instance.uuidString.lowercased())&limit=1", relativeTo: backend)!
        let (data, _) = try await URLSession.shared.data(from: url)
        return try !RuntimeJSON.decoder.decode(ReadResult<JSONValue>.self, from: data).observations.isEmpty
    }

    @Test func enableUninstallReinstallKeepIdentityAndTodayReads() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: "orisumi-integration-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        var model = Self.model(dataDirectory: directory)
        model.start()

        _ = try await Self.waitFor(model) { _ in true }
        for observer in model.observers where observer.packageId != Self.packageId {
            await model.setEnabled(observer.packageId, false)
        }

        // Data requests require a paired Runtime.
        var request = URLRequest(url: Self.backend.appending(path: "pairings"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data("{}".utf8)
        let (data, response) = try await URLSession.shared.data(for: request)
        #expect((response as? HTTPURLResponse)?.statusCode == 201)
        let pairing = try RuntimeJSON.decoder.decode(PairingCode.self, from: data)
        await model.pair(code: pairing.code, name: "Runtime integration test")
        await model.setEnabled(Self.packageId, false)
        _ = try await Self.waitFor(model) { $0.state == .stopped }
        await model.setEnabled(Self.packageId, true)
        let running = try await Self.waitFor(model) { $0.state == .running }
        let instance = running.instanceId
        #expect(running.installed && running.enabled)

        let day = Today.interval(containing: .now)
        let reader = ForegroundReader()
        let periods = try await reader.today(instanceId: instance, day: day, from: model)
        // Its first writes show the Observer has started. Let it confirm a nonempty period, then stop.
        try await Self.waitUntil { try await Self.hasSaved(instance) }
        try await Task.sleep(for: .seconds(2))
        let started = Date.now
        await model.setEnabled(Self.packageId, false)
        #expect(Date.now.timeIntervalSince(started) < 10)
        _ = try await Self.waitFor(model) { $0.state == .stopped && !$0.enabled }
        // Runtime forwards the period after the Observer has exited.
        try await Self.waitUntil {
            try await reader.today(instanceId: instance, day: day, from: model).items.count > periods.items.count
        }

        await model.setEnabled(Self.packageId, true)
        _ = try await Self.waitFor(model) { $0.state == .running }

        // Uninstall keeps the instance; reinstall reuses it and collects again.
        await model.uninstall(Self.packageId)
        let uninstalled = try await Self.waitFor(model) { !$0.installed }
        #expect(uninstalled.instanceId == instance)
        await model.install(Self.packageId)
        let reinstalled = try await Self.waitFor(model) { $0.installed && $0.state == .running }
        #expect(reinstalled.instanceId == instance)
        #expect(model.operationError == nil)

        // Other devices manage this Runtime through the backend, with the same requests the window sends.
        let management = ManagementModel(address: Self.backend)
        let host = try #require(model.host?.hostId)
        await management.refresh()
        #expect(management.error == nil)
        #expect(management.instances.contains { $0.thingId == instance && $0.hostId == host })
        await management.execute(host: host, action: "catalog")
        #expect(management.error == nil)
        #expect(management.operation?.state == "succeeded")
        #expect(management.catalog.contains { $0.id == Self.packageId })
        await management.execute(host: host, action: "resendRejected", instance: instance)
        #expect(management.error == nil)
        #expect(management.operation?.state == "succeeded")
        #expect(management.operation?.thingId == instance)

        // Runtime restarts with the same identity and only runs what was enabled.
        await model.setEnabled(Self.packageId, false)
        await model.shutdown()
        model = Self.model(dataDirectory: directory)
        model.start()
        let restarted = try await Self.waitFor(model) { $0.state == .stopped }
        #expect(restarted.instanceId == instance)
        #expect(!restarted.enabled)
        await model.shutdown()
    }
}
