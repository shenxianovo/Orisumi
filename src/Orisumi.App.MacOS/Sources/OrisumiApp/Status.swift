import Foundation

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
}

struct Agent: Decodable {
    let state: String
    let pid: Int?
    let lastExitCode: ExitCode?

    var loaded: Bool { state != "not_loaded" }
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
