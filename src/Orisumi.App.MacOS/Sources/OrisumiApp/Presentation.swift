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

    /// Collectors whose version differs from the host, which usually means a partial deploy.
    func versionDiffers(_ collector: Collector) -> Bool {
        guard let host = hostVersion, let version = collector.version else { return false }
        return host != version
    }
}

extension Collector {
    /// What the source is doing, in one short sentence ("Confirmed 3 s ago", "Not running · last exit 1").
    func sentence(now: Date) -> String {
        problemSentences(now: now).first ?? healthySentence(now: now)
    }

    /// Every problem in words, for the attention callout.
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

    private func healthySentence(now: Date) -> String {
        guard let run else { return "Running · no runs yet" }
        if run.stopped == nil {
            guard let confirmed = run.confirmed else { return "Started \(relativeDate(run.started, to: now))" }
            return "Confirmed \(relativeDate(confirmed, to: now))"
        }
        // The browser has no open run while Chrome is closed; that is not a problem.
        return "Idle · last run ended \(relativeDate(run.stopped!, to: now))"
    }

    var stateColor: Color {
        if problems.contains(where: { $0 == "run_failed" || $0 == "not_running" }) { return .red }
        return health.color
    }

    var agentText: String {
        guard let agent else { return "—" }
        switch agent.state {
        case "running": return agent.pid.map { "Running · PID \($0)" } ?? "Running"
        case "not_loaded": return "Not loaded"
        default:
            let state = humanized(agent.state)
            return agent.lastExitCode.map { "\(state) · \(exitText($0))" } ?? state
        }
    }
}

extension Run {
    var isOpen: Bool { stopped == nil }
    var end: Date? { stopped ?? confirmed }

    /// "13:29 – now", "Nov 19, 01:29 – 12:29"
    func spanText(now: Date) -> String {
        let start = timeText(started, now: now)
        guard let stopped else { return "\(start) – now" }
        let sameDay = Calendar.current.isDate(started, inSameDayAs: stopped)
        return "\(start) – \(sameDay ? clockText(stopped) : timeText(stopped, now: now))"
    }

    func durationText(now: Date) -> String {
        guard let end = stopped ?? confirmed.map({ max($0, now) }) ?? Optional(now), end >= started else { return "—" }
        return elapsedText(from: started, to: end)
    }
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
