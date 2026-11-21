import Foundation

struct Coverage: Decodable {
    let sources: [SourceEvidence]

    func runs(in interval: Range<Date>) -> [RunSpan] {
        sources.flatMap { source in
            source.evidence.compactMap { row in
                guard row.isRun(for: source.source), let runID = row.runId else { return nil }
                let start = max(interval.lowerBound, row.started)
                let end = min(interval.upperBound, row.stopped ?? row.confirmed ?? interval.upperBound)
                guard start < end else { return nil }
                return RunSpan(source: source.source, database: source.database, runID: runID,
                               start: start, end: end, row: row)
            }
        }
    }

    /// The source's own lane: when it ran, where a run failed, and when a read failed.
    func lane(for source: String, day: DateInterval, now: Date) -> [Mark] {
        let spans = runs(in: day.start..<now).filter { $0.source == source }
        var marks = merged(spans.map { Mark(start: $0.start, end: openEnd($0, now: now), kind: .on, help: "") }, now: now)
        for span in spans where span.row.evidence == "failed" {
            marks.append(Mark(start: span.end, end: span.end, kind: .failure,
                              help: ["Run failed at \(clockText(span.end))", span.row.detail].compactMap { $0 }.joined(separator: "\n")))
        }
        for row in rows(of: source) where row.evidence == "read_failed" && day.contains(row.started) {
            marks.append(Mark(start: row.started, end: row.started, kind: .warning,
                              help: ["Read failed at \(clockText(row.started))", row.detail].compactMap { $0 }.joined(separator: "\n")))
        }
        return marks
    }

    /// One lane per channel that reports availability, such as macOS window titles or a browser profile's tabs.
    func channelLanes(for source: String, day: DateInterval, now: Date) -> [Lane] {
        // An open row lasts as long as its run; a run that is still confirmed lasts until now.
        let runEnds = Dictionary(runs(in: day.start..<now).filter { $0.source == source }.map { ($0.runID, openEnd($0, now: now)) },
                                 uniquingKeysWith: max)
        let rows = rows(of: source).filter { Self.availability.contains($0.evidence) || Self.gaps.contains($0.evidence) }
        // While the session is locked there is nothing to read; that shows as a plain gap, not a warning.
        let expected = { (row: Evidence) in row.evidence == "unavailable" && row.detail == "session_paused" }
        return Dictionary(grouping: rows, by: \.channelID).compactMap { id, rows -> Lane? in
            let marks = rows.compactMap { row -> Mark? in
                guard !expected(row) else { return nil }
                let start = max(day.start, row.started)
                let end = min(now, row.stopped ?? row.runId.flatMap { runEnds[$0] } ?? row.started)
                guard end >= start, start < now else { return nil }
                let on = row.evidence == "available"
                let what = on ? "Available" : row.evidence == "unavailable" ? "Unavailable" : humanized(row.evidence)
                let span = end > start ? "\(clockText(start)) – \(row.stopped == nil && end == now ? "now" : clockText(end))" : clockText(start)
                return Mark(start: start, end: end, kind: on ? .on : .warning,
                            help: [span, [what, row.detail.map(humanized)].compactMap { $0 }.joined(separator: " · ")].joined(separator: "\n"))
            }
            guard !marks.isEmpty, let first = rows.first else { return nil }
            let latest = rows.max { $0.started < $1.started }!
            let current = latest.stopped == nil && latest.evidence != "available" && !expected(latest)
                ? [latest.channelName, latest.detail.map(humanized)].compactMap { $0 }.joined(separator: ": ") : nil
            return Lane(id: id, scope: first.scope, name: first.channelName, marks: merged(marks, now: now), currentProblem: current)
        }
        .sorted { ($0.order, $0.name) < ($1.order, $1.name) }
    }

    private static let availability: Set<String> = ["available", "unavailable", "snapshot_timeout"]
    private static let gaps: Set<String> = ["delivery_rejected", "reconciliation_changed"]

    private func rows(of source: String) -> [Evidence] {
        sources.filter { $0.source == source }.flatMap(\.evidence)
    }

    /// Coverage is read once a minute; a run confirmed within the stale limit is drawn up to now.
    private func openEnd(_ span: RunSpan, now: Date) -> Date {
        guard span.row.stopped == nil, now.timeIntervalSince(span.end) < 300 else { return span.end }
        return now
    }

    /// Joins available spans that touch, so restarts and run boundaries don't break the bar, and
    /// describes each joined span by its own start and end.
    private func merged(_ marks: [Mark], now: Date) -> [Mark] {
        var result: [Mark] = []
        for mark in marks.sorted(by: { $0.start < $1.start }) {
            if mark.kind == .on, let last = result.last, last.kind == .on, mark.start.timeIntervalSince(last.end) < 60 {
                result[result.count - 1] = Mark(start: last.start, end: max(last.end, mark.end), kind: .on, help: "")
            } else {
                result.append(mark)
            }
        }
        return result.map { mark in
            guard mark.kind == .on else { return mark }
            let end = mark.end == now ? "now" : clockText(mark.end)
            return Mark(start: mark.start, end: mark.end, kind: .on,
                        help: "\(clockText(mark.start)) – \(end) · \(elapsedText(from: mark.start, to: mark.end))")
        }
    }
}

struct SourceEvidence: Decodable {
    let source: String
    let database: String?
    let evidence: [Evidence]
}

struct Evidence: Decodable {
    let scope: String
    let mode: String
    let startedAtUtcMs: Int64
    let stoppedAtUtcMs: Int64?
    let evidence: String
    let detail: String?
    let runId: Int64?
    let confirmedUntilUtcMs: Int64?
    let profileId: String?
    let browser: String?

    var started: Date { date(milliseconds: startedAtUtcMs) }
    var stopped: Date? { stoppedAtUtcMs.map(date(milliseconds:)) }
    var confirmed: Date? { confirmedUntilUtcMs.map(date(milliseconds:)) }

    func isRun(for source: String) -> Bool {
        // These are the capture_runs arms of the four capture_coverage views. The
        // same scopes also carry read failures, and run_id is shared by channel rows.
        let runScopes = ["macos": "foreground", "browser": "browser", "claude": "project_logs", "codex": "import"]
        return runId != nil && ["unconfirmed", "completed", "failed"].contains(evidence)
            && (runScopes[source].map { $0 == scope } ?? true)
    }

    var channelID: String { [browser, profileId, scope].compactMap { $0 }.joined(separator: "/") }

    var channelName: String {
        let name = channelTitle(scope)
        guard let profileId else { return name }
        let profile = profileId.count > 12 ? "Profile \(profileId.prefix(8))" : profileId
        return scope == "browser" ? profile : "\(name) · \(profile)"
    }
}

struct RunSpan: Identifiable {
    let source: String
    let database: String?
    let runID: Int64
    let start: Date
    let end: Date
    let row: Evidence

    var id: String { "\(source)/\(database ?? "")/\(runID)" }
}

/// A span or an instant on a timeline lane.
struct Mark: Identifiable, Equatable {
    enum Kind { case on, warning, failure }

    let start: Date
    let end: Date
    let kind: Kind
    let help: String

    var id: String { "\(kind)/\(start.timeIntervalSince1970)/\(end.timeIntervalSince1970)" }
}

struct Lane: Identifiable {
    let id: String
    let scope: String
    let name: String
    let marks: [Mark]
    /// Set while the channel's latest record says it is not available.
    let currentProblem: String?

    var order: Int { channelOrder.firstIndex(of: scope) ?? channelOrder.count }
}
