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

    func channels(for name: String) -> [Channel] {
        let rows = sources.filter { $0.source == name }.flatMap(\.evidence)
        let channelRows = rows.filter { row in
            switch row.evidence {
            case "available", "unavailable", "method_selected", "snapshot_timeout",
                 "reconciliation_changed", "delivery_rejected", "range_read", "observed": true
            default: name == "browser" && row.isRun(for: name) && row.profileId != nil
            }
        }
        return Dictionary(grouping: channelRows, by: \.channelID).values.compactMap { history -> Channel? in
            let sorted = history.sorted { $0.started > $1.started }
            guard let latest = sorted.first else { return nil }
            let availability = sorted.first { $0.evidence != "method_selected" && $0.runId == latest.runId }
            return Channel(id: latest.channelID, name: latest.channelName, method: humanized(latest.mode),
                           status: availability?.channelStatus ?? "Method selected", detail: availability?.detail.map(humanized),
                           unavailable: availability?.channelStatus == "Unavailable")
        }.sorted {
            if $0.unavailable != $1.unavailable { return $0.unavailable }
            return $0.name.localizedStandardCompare($1.name) == .orderedAscending
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
        let name = humanized(scope)
        guard let profileId else { return name }
        let profile = "Profile \(profileId.prefix(8))"
        return scope == "browser" ? profile : "\(name) · \(profile)"
    }

    var channelStatus: String {
        switch evidence {
        case "snapshot_timeout": stopped == nil ? "Unavailable" : "Snapshot timeout"
        case "unconfirmed": "Available"
        case "completed", "failed": "Unavailable"
        default: humanized(evidence)
        }
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

struct Channel: Identifiable {
    let id: String
    let name: String
    let method: String
    let status: String
    let detail: String?
    let unavailable: Bool
}
