import SwiftUI
import XCTest
@testable import OrisumiApp

final class AppTests: XCTestCase {
    func testStatusContract() throws {
        let status: CaptureStatus = try fixture("status")
        XCTAssertEqual(status.instance, "app_fixture")
        XCTAssertEqual(status.health, .attention)
        XCTAssertTrue(status.anyLoaded)
        XCTAssertEqual(shortVersion(status.hostVersion), "0.1.0+321c68f")
        XCTAssertEqual(status.collectors.map(\.displayName), ["Browser", "Claude Code", "Codex", "macOS"])
        XCTAssertEqual(status.collectors[0].agent?.lastExitCode?.value, "terminated by signal")
        XCTAssertNil(status.collectors[2].agent?.lastExitCode)
        XCTAssertEqual(status.collectors[3].run?.runId, 2)
        XCTAssertEqual(status.collectors[3].recentRuns.last?.stopReason, "completed")
        XCTAssertEqual(status.collectors[3].recentReadFailures.first?.scope, "windows")
    }

    func testStatusRequiresCurrentHealthAndHistoryFields() throws {
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: fixtureData("status")) as? [String: Any])
        for key in ["health", "generated_at_utc_ms"] {
            var status = json
            status.removeValue(forKey: key)
            XCTAssertThrowsError(try decodeStatus(status), key)
        }
        for key in ["health", "problems", "recent_runs", "recent_read_failures"] {
            var status = json
            var collectors = try XCTUnwrap(json["collectors"] as? [[String: Any]])
            collectors[0].removeValue(forKey: key)
            status["collectors"] = collectors
            XCTAssertThrowsError(try decodeStatus(status), key)
        }
    }

    func testConfirmationAfterStatusGenerationShowsNow() throws {
        let status: CaptureStatus = try fixture("status")
        let collector = try XCTUnwrap(status.collectors.last)
        let confirmed = try XCTUnwrap(collector.run?.confirmed)
        XCTAssertGreaterThan(confirmed, status.generatedAt)
        XCTAssertEqual(collector.menuState(now: status.generatedAt), "confirmed now")
        XCTAssertEqual(relativeDate(status.generatedAt, to: status.generatedAt), "now")
    }

    func testHealthPresentation() throws {
        XCTAssertEqual(Health.ok.symbol, "record.circle")
        XCTAssertEqual(Health.attention.symbol, "exclamationmark.circle")
        XCTAssertEqual(Health.stopped.symbol, "pause.circle")
        XCTAssertEqual(Health.unavailable.symbol, "questionmark.circle")
        XCTAssertEqual(Health.ok.color, .green)
        XCTAssertEqual(Health.attention.color, .yellow)
        XCTAssertEqual(Health.stopped.color, .secondary)
        let status: CaptureStatus = try fixture("status")
        XCTAssertEqual(status.collectors[0].stateColor, .red)
        XCTAssertEqual(status.collectors[2].readFailuresLastHour, 1)
        XCTAssertEqual(status.collectors[2].health, .ok)
        XCTAssertEqual(status.collectors[2].stateColor, .green)
    }

    func testCoverageSpansAndChannels() throws {
        let coverage: Coverage = try fixture("coverage")
        let midnight = try Date("2026-11-19T16:00:00Z", strategy: .iso8601)
        let now = midnight.addingTimeInterval(23 * 3600)
        let spans = coverage.runs(in: midnight..<now)
        XCTAssertEqual(spans.count, 8)
        XCTAssertEqual(spans.filter { $0.start == midnight }.count, 3)
        let macOS = try XCTUnwrap(spans.first { $0.source == "macos" && $0.runID == 2 })
        XCTAssertEqual(macOS.end, now.addingTimeInterval(-6))
        let failed = try XCTUnwrap(spans.first { $0.source == "browser" && $0.runID == 2 })
        XCTAssertEqual(failed.end, now.addingTimeInterval(-7200))
        XCTAssertEqual(failed.row.evidence, "failed")
        XCTAssertEqual(failed.row.profileId, "Default")

        let channels = coverage.channels(for: "macos")
        XCTAssertEqual(channels.map(\.name), ["Input", "Microphone", "Windows"])
        XCTAssertEqual(channels.first?.detail, "permission_denied")
        XCTAssertEqual(channels.last?.status, "Available")
        XCTAssertEqual(channels.last?.method, "snapshot")
    }

    func testInstalledDiscoveryResolvesApplicationSymlinkAndHonorsOverrides() throws {
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = temp.appendingPathComponent("Instance Store")
        let bundle = store.appendingPathComponent("app/host/Orisumi.app")
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temp) }
        let link = temp.appendingPathComponent("Orisumi.app")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: bundle)

        let cli = try CLIConfiguration(arguments: [], bundle: link)
        XCTAssertEqual(cli.store?.path, store.resolvingSymlinksInPath().path)
        XCTAssertEqual(cli.executable.path, cli.store!.appendingPathComponent("app/host/Orisumi.app/Contents/MacOS/orisumi").path)
        let override = try CLIConfiguration(arguments: ["--cli", "/tmp/custom host", "--store", "/tmp/custom store"], bundle: bundle)
        XCTAssertEqual(override.executable.path, "/tmp/custom host")
        XCTAssertEqual(override.store?.path, "/tmp/custom store")
        XCTAssertNil(try CLIConfiguration(arguments: ["--cli", "/tmp/orisumi-dev"], bundle: temp).store)
        XCTAssertThrowsError(try CLIConfiguration(arguments: [], bundle: temp))
    }

    private var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }

    private func fixture<T: Decodable>(_ name: String) throws -> T {
        try decoder.decode(T.self, from: fixtureData(name))
    }

    private func decodeStatus(_ json: [String: Any]) throws -> CaptureStatus {
        try decoder.decode(CaptureStatus.self, from: JSONSerialization.data(withJSONObject: json))
    }

    private func fixtureData(_ name: String) throws -> Data {
        let url = try XCTUnwrap(Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }
}
