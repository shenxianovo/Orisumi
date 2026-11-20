import Foundation
import SwiftUI

enum Health: String, Decodable {
    case ok, attention, stopped, unavailable

    var symbol: String {
        switch self {
        case .ok: "record.circle"
        case .attention: "exclamationmark.circle"
        case .stopped: "pause.circle"
        case .unavailable: "questionmark.circle"
        }
    }

    var title: String {
        switch self {
        case .ok: "Capturing"
        case .attention: "Needs attention"
        case .stopped: "Capture stopped"
        case .unavailable: "Status unavailable"
        }
    }

    var color: Color {
        switch self {
        case .ok: .green
        case .attention: .yellow
        case .stopped, .unavailable: .secondary
        }
    }
}

struct CaptureStatus: Decodable {
    let instance: String
    let store: String
    let hostVersion: String?
    let generatedAtUtcMs: Int64
    let health: Health
    let collectors: [Collector]

    var generatedAt: Date { date(milliseconds: generatedAtUtcMs) }

    var anyLoaded: Bool { collectors.contains { $0.agent?.loaded == true } }

    var summary: String {
        guard health == .ok else { return health.title }
        let count = collectors.count
        return "Capturing · \(count) \(count == 1 ? "source" : "sources")"
    }
}

struct Collector: Decodable, Identifiable {
    let name: String
    let version: String?
    let health: Health
    let problems: [String]
    let agent: Agent?
    let run: Run?
    let readFailuresLastHour: Int?
    let recentRuns: [Run]
    let recentReadFailures: [ReadFailure]

    var id: String { name }

    var displayName: String {
        switch name {
        case "macos": "macOS"
        case "browser": "Browser"
        case "claude": "Claude Code"
        case "codex": "Codex"
        default: name.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }

    var problemLabels: [String] {
        problems.map {
            switch $0 {
            case "not_installed": "Not installed"
            case "not_loaded": "Not loaded"
            case "not_running": "Not running"
            case "run_failed": "Run failed"
            case "stale": "Confirmation is stale"
            default: words($0).capitalized
            }
        }
    }

    var stateLabel: String {
        if health == .stopped { return "Stopped" }
        if let problem = problemLabels.first { return problem }
        return health.title
    }

    var stateColor: Color {
        if problems.contains(where: { $0 == "run_failed" || $0 == "not_running" }) { return .red }
        return health.color
    }

    func menuState(now: Date) -> String {
        if health == .ok, let confirmed = run?.confirmed {
            return "confirmed \(relativeDate(confirmed, to: now))"
        }
        return stateLabel.prefix(1).lowercased() + String(stateLabel.dropFirst())
    }
}

struct Agent: Decodable {
    let state: String
    let pid: Int?
    let lastExitCode: ExitCode?

    var loaded: Bool { state != "not_loaded" }

    var displayState: String {
        let label = words(state).capitalized
        if let pid { return "\(label) (PID \(pid))" }
        if let lastExitCode { return "\(label) (last exit \(lastExitCode.value))" }
        return label
    }
}

struct ExitCode: Decodable {
    let value: String

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let number = try? container.decode(Int.self) {
            value = String(number)
        } else {
            value = try container.decode(String.self)
        }
    }
}

struct Run: Decodable, Identifiable {
    let runId: Int64
    let startedAtUtcMs: Int64
    let collectorVersion: String?
    let confirmedUntilUtcMs: Int64?
    let stoppedAtUtcMs: Int64?
    let stopReason: String?
    let error: String?
    let database: String?

    var id: String { "\(database ?? "")/\(runId)" }
    var started: Date { date(milliseconds: startedAtUtcMs) }
    var stopped: Date? { stoppedAtUtcMs.map(date(milliseconds:)) }
    var confirmed: Date? { confirmedUntilUtcMs.map(date(milliseconds:)) }
}

struct ReadFailure: Decodable {
    let observedAtUtcMs: Int64
    let runId: Int64
    let scope: String?
    let error: String
    let database: String?

    var observed: Date { date(milliseconds: observedAtUtcMs) }
}

func date(milliseconds: Int64) -> Date {
    Date(timeIntervalSince1970: Double(milliseconds) / 1000)
}

func relativeDate(_ date: Date, to now: Date) -> String {
    guard now.timeIntervalSince(date) >= 1 else { return "now" }
    let formatter = RelativeDateTimeFormatter()
    formatter.unitsStyle = .abbreviated
    return formatter.localizedString(for: date, relativeTo: now)
}

func absoluteDate(_ date: Date?) -> String {
    date?.formatted(date: .abbreviated, time: .shortened) ?? "—"
}

func tableDate(_ date: Date?) -> Text {
    guard let date else { return Text("—") }
    let calendar = Calendar.current
    if calendar.isDateInToday(date) { return Text(date.formatted(date: .omitted, time: .shortened)) }
    if calendar.isDate(date, equalTo: .now, toGranularity: .year) {
        return Text(date.formatted(.dateTime.month().day().hour().minute()))
    }
    return Text(date.formatted(date: .numeric, time: .shortened))
}

func shortVersion(_ version: String?) -> String {
    guard let version else { return "—" }
    guard let separator = version.lastIndex(of: "+") else { return version }
    let revision = version[version.index(after: separator)...]
    guard revision.allSatisfy(\.isHexDigit) else { return version }
    return String(version[...separator]) + revision.prefix(7)
}

func words(_ value: String) -> String {
    value.replacingOccurrences(of: "_", with: " ")
}
