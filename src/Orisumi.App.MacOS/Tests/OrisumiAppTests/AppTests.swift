import SwiftUI
import XCTest
@testable import OrisumiApp

final class AppTests: XCTestCase {
    func testStatusContract() throws {
        let status: CaptureStatus = try fixture("status")
        XCTAssertEqual(status.instance, "app_fixture")
        XCTAssertEqual(status.health, .attention)
        XCTAssertTrue(status.anyLoaded)
        XCTAssertEqual(shortVersion(status.hostVersion), "0.1.0 (321c68f)")
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
        XCTAssertEqual(collector.summary(now: status.generatedAt), "Capturing")
        XCTAssertTrue(collector.diagnostics(now: status.generatedAt).hasSuffix("confirmed now"))
        XCTAssertEqual(relativeDate(status.generatedAt, to: status.generatedAt), "now")
    }

    func testHealthPresentation() throws {
        XCTAssertEqual(Health.ok.symbol, "record.circle")
        XCTAssertEqual(Health.attention.symbol, "exclamationmark.circle")
        XCTAssertEqual(Health.stopped.symbol, "pause.circle")
        XCTAssertEqual(Health.unavailable.symbol, "questionmark.circle")
        let status: CaptureStatus = try fixture("status")
        XCTAssertEqual(status.collectors[0].severity, .failure)
        XCTAssertEqual(status.severity, .failure)
        XCTAssertEqual(status.collectors[2].readFailuresLastHour, 1)
        XCTAssertEqual(status.collectors[2].health, .ok)
        XCTAssertEqual(status.collectors[2].severity, .ok)
        XCTAssertEqual(try withHealth(status, ["stopped", "stopped", "stopped", "stopped"]).severity, .stopped)
        XCTAssertEqual(try withHealth(status, ["ok", "stopped", "attention", "ok"]).severity, .warning)
    }

    func testCoverageLanes() throws {
        let coverage: Coverage = try fixture("coverage")
        let midnight = try Date("2026-11-19T16:00:00Z", strategy: .iso8601)
        let day = DateInterval(start: midnight, duration: 24 * 3600)
        let now = midnight.addingTimeInterval(23 * 3600)
        let spans = coverage.runs(in: midnight..<now)
        XCTAssertEqual(spans.count, 8)
        XCTAssertEqual(spans.filter { $0.start == midnight }.count, 3)
        let failed = try XCTUnwrap(spans.first { $0.source == "browser" && $0.runID == 2 })
        XCTAssertEqual(failed.end, now.addingTimeInterval(-7200))
        XCTAssertEqual(failed.row.profileId, "Default")

        // A failed run keeps its bar; the failure is a tick where it ended.
        let browser = coverage.lane(for: "browser", day: day, now: now)
        XCTAssertEqual(browser.map(\.kind), [.on, .on, .failure])
        XCTAssertEqual(browser.last?.start, failed.end)
        // A run confirmed seconds ago reaches now; a read failure is an instant.
        let macOS = coverage.lane(for: "macos", day: day, now: now)
        XCTAssertEqual(macOS.map(\.kind), [.on, .on, .warning])
        XCTAssertEqual(macOS[1].end, now)
        XCTAssertEqual(macOS[2].start, macOS[2].end)

        let lanes = coverage.channelLanes(for: "macos", day: day, now: now)
        XCTAssertEqual(lanes.map(\.name), ["Windows", "Keyboard & mouse", "Microphone"])
        XCTAssertEqual(lanes[1].currentProblem, "Keyboard & mouse: Permission denied")
        XCTAssertEqual(lanes[1].marks.map(\.kind), [.warning])
        XCTAssertEqual(lanes[1].marks.first?.end, now)
        XCTAssertNil(lanes[0].currentProblem)
        let tabs = try XCTUnwrap(coverage.channelLanes(for: "browser", day: day, now: now).first)
        XCTAssertEqual(tabs.name, "Tabs · Default")
        XCTAssertNil(tabs.currentProblem)
        XCTAssertEqual(tabs.marks.map(\.kind), [.warning])
        XCTAssertTrue(coverage.channelLanes(for: "claude", day: day, now: now).isEmpty)
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

    func testHeadlines() throws {
        let status: CaptureStatus = try fixture("status")
        XCTAssertEqual(status.headline, "Browser needs attention")
        XCTAssertEqual(try withHealth(status, ["ok", "ok", "ok", "ok"]).headline, "All 4 sources capturing")
        XCTAssertEqual(try withHealth(status, ["stopped", "stopped", "stopped", "stopped"]).headline, "Capture stopped")
        XCTAssertEqual(try withHealth(status, ["ok", "stopped", "ok", "ok"]).headline, "Claude Code stopped")
        XCTAssertEqual(try withHealth(status, ["stopped", "stopped", "ok", "ok"]).headline, "2 of 4 sources stopped")
        XCTAssertEqual(try withHealth(status, ["attention", "stopped", "ok", "ok"]).headline, "2 sources need attention")
    }

    func testSentences() throws {
        let status: CaptureStatus = try fixture("status")
        let browser = status.collectors[0]
        XCTAssertEqual(browser.problemSentences(now: status.generatedAt),
                       ["Not running · terminated by signal", "Last run failed: Browser connection ended unexpectedly."])
        XCTAssertEqual(browser.summary(now: status.generatedAt), "Not running")
        XCTAssertEqual(browser.agentText, "Restarting · terminated by signal")
        XCTAssertEqual(browser.recentIssues.map(\.title), ["Run failed"])
        XCTAssertEqual(status.collectors[3].recentIssues.map(\.title), ["Windows"])
        XCTAssertEqual(exitText(try decodeExit("1")), "exited with code 1")
        XCTAssertEqual(humanized("permission_denied"), "Permission denied")
        XCTAssertEqual(humanized("some_new_code"), "Some new code")
        XCTAssertNil(status.collectorVersionNote)
    }

    private func withHealth(_ status: CaptureStatus, _ health: [String]) throws -> CaptureStatus {
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: fixtureData("status")) as? [String: Any])
        json["collectors"] = try XCTUnwrap(json["collectors"] as? [[String: Any]]).enumerated().map { index, collector in
            var collector = collector
            collector["health"] = health[index]
            collector["problems"] = health[index] == "ok" ? [] : health[index] == "stopped" ? ["not_loaded"] : ["stale"]
            return collector
        }
        return try decodeStatus(json)
    }

    private func decodeExit(_ number: String) throws -> ExitCode {
        try JSONDecoder().decode(ExitCode.self, from: Data(number.utf8))
    }

}
