import Foundation
import SwiftUI

// Words and formats shared by the window and the menu bar. Kept free of views so they can be tested.

/// Dates and durations follow the language the interface is shown in, so an English UI
/// never mixes in another language's "3秒前"; the region still decides 12/24-hour time.
let appLocale: Locale = {
    let language = Bundle.main.preferredLocalizations.first ?? "en"
    var components = Locale.Components(languageCode: Locale.LanguageCode(language))
    components.region = Locale.current.region
    return Locale(components: components)
}()

extension CaptureStatus {
    /// One sentence that says what is going on, used as the window headline and the first menu line.
    var headline: String {
        let total = collectors.count
        guard total > 0 else { return "No sources installed" }
        let unhealthy = collectors.filter { $0.health != .ok }
        let stopped = unhealthy.filter { $0.health == .stopped }
        if unhealthy.isEmpty { return total == 1 ? "\(collectors[0].displayName) capturing" : "All \(total) sources capturing" }
        if stopped.count == total { return "Capture stopped" }
        if stopped.count == unhealthy.count {
            return stopped.count == 1 ? "\(stopped[0].displayName) stopped" : "\(stopped.count) of \(total) sources stopped"
        }
        return unhealthy.count == 1 ? "\(unhealthy[0].displayName) needs attention" : "\(unhealthy.count) sources need attention"
    }
}

/// How bad a source's state is, from its problems; decides its dot, symbol and colour everywhere.
enum Severity: Comparable {
    case ok, stopped, warning, failure

    var color: Color {
        switch self {
        case .ok: .green
        case .stopped: .secondary
        case .warning: .orange
        case .failure: .red
        }
    }

    var symbol: String {
        switch self {
        case .ok: "checkmark.circle.fill"
        case .stopped: "pause.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .failure: "xmark.octagon.fill"
        }
    }
}

extension CaptureStatus {
    var severity: Severity { collectors.map(\.severity).max() ?? .stopped }

    /// The other version the collectors run, when they all differ from the host the same way.
    /// `orisumi-dev deploy` can release the host alone, so this is information, not a problem.
    var collectorVersionNote: String? {
        let versions = Set(collectors.compactMap(\.version)).subtracting([hostVersion].compactMap { $0 })
        guard !versions.isEmpty else { return nil }
        return versions.count == 1 ? "collectors \(shortVersion(versions.first))" : "collectors on \(versions.count) other versions"
    }
}

extension Collector {
    var severity: Severity {
        if problems.contains(where: { $0 == "run_failed" || $0 == "not_running" }) { return .failure }
        if problems.isEmpty { return .ok }
        return problems.allSatisfy { $0 == "not_loaded" } ? .stopped : .warning
    }

    /// What the source is doing in a few words, for its row and the menu ("Capturing", "Not running").
    func summary(now: Date) -> String {
        if let problem = problems.first {
            switch problem {
            case "not_loaded": return "Stopped"
            case "not_installed": return "Not installed"
            case "not_running": return "Not running"
            case "run_failed": return "Last run failed"
            case "stale":
                guard let run else { return "No recent confirmation" }
                return "No confirmation for \(elapsedText(from: run.confirmed ?? run.started, to: now))"
            default: return humanized(problem)
            }
        }
        guard let run, run.isOpen else {
            // The browser has no open run while Chrome is closed; that is not a problem.
            return name == "browser" ? "Waiting for Chrome" : "Idle"
        }
        return "Capturing"
    }

    /// Every problem in words, for the headline and the expanded row.
    func problemSentences(now: Date) -> [String] {
        problems.map { problem in
            switch problem {
            case "not_loaded": "Stopped"
            case "not_installed": "Not installed · data only"
            case "not_running": ["Not running", agent?.lastExitCode.map(exitText)].compactMap { $0 }.joined(separator: " · ")
            case "run_failed":
                if let error = run?.error?.split(whereSeparator: \.isNewline).first { "Last run failed: \(error)" }
                else { "Last run failed" }
            case "stale":
                if let run { "No confirmation for \(elapsedText(from: run.confirmed ?? run.started, to: now))" }
                else { "No recent confirmation" }
            default: humanized(problem)
            }
        }
    }

    /// Process and run facts for diagnosing, in one line.
    func diagnostics(now: Date) -> String {
        var parts = [agentText]
        parts.append(shortVersion(version))
        if let run {
            if run.isOpen {
                parts.append("run \(run.runId) since \(timeText(run.started, now: now))")
                if let confirmed = run.confirmed { parts.append("confirmed \(relativeDate(confirmed, to: now))") }
            } else if let stopped = run.stopped {
                parts.append("last run ended \(timeText(stopped, now: now))")
            }
        }
        return parts.joined(separator: " · ")
    }

    /// Failed runs and read failures, newest first; a repeat of the one before it is counted, not listed again.
    var recentIssues: [Issue] {
        let runs = recentRuns.filter { $0.stopReason == "failed" }.map { run in
            Issue(date: run.stopped ?? run.started, title: "Run failed", text: run.error ?? "No error recorded")
        }
        let reads = recentReadFailures.map { failure in
            Issue(date: failure.observed, title: failure.scope.map(channelTitle) ?? "Read failed", text: failure.error)
        }
        var issues: [Issue] = []
        for issue in (runs + reads).sorted(by: { $0.date > $1.date }) {
            if let last = issues.last, last.title == issue.title, last.text == issue.text {
                issues[issues.count - 1].count += 1
            } else {
                issues.append(issue)
            }
        }
        return issues
    }

    var agentText: String {
        guard let agent else { return "—" }
        switch agent.state {
        case "running": return agent.pid.map { "PID \($0)" } ?? "Running"
        case "not_loaded": return "Not loaded"
        default:
            let state = humanized(agent.state)
            return agent.lastExitCode.map { "\(state) · \(exitText($0))" } ?? state
        }
    }
}

struct Issue: Hashable {
    /// The latest time it happened.
    let date: Date
    let title: String
    let text: String
    var count = 1
}

/// Order of channels in an expanded source: what you did first, then the devices around it.
let channelOrder = ["applications", "windows", "window_title", "window_focus", "input", "afk",
                    "audio_output", "microphone", "camera", "browser", "tabs", "history", "bookmarks", "downloads"]

func channelTitle(_ scope: String) -> String {
    let known = [
        "applications": "Apps",
        "windows": "Windows",
        "window_title": "Window titles",
        "window_focus": "Window focus",
        "input": "Keyboard & mouse",
        "afk": "Away from keyboard",
        "audio_output": "Audio output",
        "foreground": "Foreground",
        "project_logs": "Project logs",
        "rollout": "Session logs",
    ]
    return known[scope] ?? humanized(scope)
}

extension Run {
    var isOpen: Bool { stopped == nil }
}

/// launchd reports a number or a phrase such as "terminated by signal".
func exitText(_ code: ExitCode) -> String {
    Int(code.value) == nil ? code.value : "exited with code \(code.value)"
}

/// Turns codes such as `permission_denied` into "Permission denied".
func humanized(_ code: String) -> String {
    let known = [
        "permission_denied": "Permission denied",
        "permission_granted": "Permission granted",
        "listener_unavailable": "Listener unavailable",
        "listener_started": "Listening",
        "snapshot_received": "Reading",
        "sample_received": "Reading",
        "read_failed": "Read failed",
        "focus_unavailable": "Focus unreadable",
        "no_foreground_application": "No foreground app",
        "service_restarted": "Service restarted",
        "method_selected": "Method selected",
        "snapshot_timeout": "Snapshot timed out",
        "reconciliation_changed": "Reconciled",
        "delivery_rejected": "Delivery rejected",
        "range_read": "Read history",
        "spawn scheduled": "Restarting",
        "not_loaded": "Not loaded",
    ]
    if let text = known[code] { return text }
    let words = code.replacingOccurrences(of: "_", with: " ")
    return words.prefix(1).uppercased() + words.dropFirst()
}

func relativeDate(_ date: Date, to now: Date) -> String {
    guard now.timeIntervalSince(date) >= 1 else { return "now" }
    let formatter = RelativeDateTimeFormatter()
    formatter.locale = appLocale
    formatter.unitsStyle = .abbreviated
    return formatter.localizedString(for: date, relativeTo: now)
}

func elapsedText(from start: Date, to end: Date) -> String {
    let formatter = DateComponentsFormatter()
    var calendar = Calendar.current
    calendar.locale = appLocale
    formatter.calendar = calendar
    formatter.allowedUnits = end.timeIntervalSince(start) < 60 ? [.second] : [.day, .hour, .minute]
    formatter.unitsStyle = .abbreviated
    formatter.maximumUnitCount = 2
    return formatter.string(from: start, to: end) ?? "—"
}

/// Clock time today, date and time otherwise.
func timeText(_ date: Date, now: Date) -> String {
    Calendar.current.isDate(date, inSameDayAs: now)
        ? clockText(date)
        : date.formatted(.dateTime.month(.abbreviated).day().hour().minute().locale(appLocale))
}

func clockText(_ date: Date) -> String {
    date.formatted(.dateTime.hour().minute().locale(appLocale))
}

func absoluteDate(_ date: Date?) -> String {
    date?.formatted(.dateTime.year().month(.abbreviated).day().hour().minute().second().locale(appLocale)) ?? "—"
}

func shortVersion(_ version: String?) -> String {
    guard let version else { return "—" }
    guard let separator = version.lastIndex(of: "+") else { return version }
    let revision = version[version.index(after: separator)...]
    guard revision.allSatisfy(\.isHexDigit) else { return version }
    return "\(version[..<separator]) (\(revision.prefix(7)))"
}
