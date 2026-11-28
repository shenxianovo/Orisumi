import AppKit
import SwiftUI

/// What opens under a source in Today: its channels on the same axis, what went wrong recently,
/// and the process facts with the log and database a click away.
struct SourceDetail: View {
    let collector: Collector
    let status: CaptureStatus
    let day: DateInterval
    let lanes: [Lane]

    private var now: Date { status.generatedAt }
    private let indent: CGFloat = 30

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if !lanes.isEmpty {
                VStack(spacing: 0) {
                    ForEach(lanes) { lane in
                        HStack(spacing: 12) {
                            Text(lane.name)
                                .font(.callout)
                                .foregroundStyle(lane.currentProblem != nil && collector.severity == .ok
                                                 ? AnyShapeStyle(Color.orange) : AnyShapeStyle(.secondary))
                                .lineLimit(1)
                                .frame(width: Overview.labelWidth - indent, alignment: .leading)
                            Track(day: day, now: now, marks: lane.marks, height: 6, tint: .accentColor.opacity(0.5))
                        }
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel(lane.name)
                        .accessibilityValue(lane.currentProblem ?? "Available")
                    }
                }
            }

            if !problems.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(problems, id: \.text) { problem in
                        Label {
                            Text(problem.text).textSelection(.enabled)
                        } icon: {
                            Image(systemName: problem.severity.symbol).foregroundStyle(problem.severity.color)
                        }
                    }
                }
            }

            if !issues.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Recent Issues").font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                    Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 12, verticalSpacing: 5) {
                        ForEach(issues, id: \.self) { issue in
                            GridRow {
                                Text(timeText(issue.date, now: now))
                                    .monospacedDigit()
                                    .foregroundStyle(.secondary)
                                    .help(absoluteDate(issue.date))
                                Text(issue.count > 1 ? "\(issue.title) ×\(issue.count)" : issue.title)
                                Text(issue.text)
                                    .foregroundStyle(.secondary)
                                    .textSelection(.enabled)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                    }
                    .font(.callout)
                }
            }

            HStack(spacing: 14) {
                Text(collector.diagnostics(now: now))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .help(collector.version ?? "")
                Spacer(minLength: 12)
                Button("Show Log", action: openLogs)
                    .disabled(logs.isEmpty)
                    .help(logs.isEmpty ? "No log files yet" : logs.map(\.lastPathComponent).joined(separator: ", "))
                Button("Reveal Database") { NSWorkspace.shared.activateFileViewerSelecting([database]) }
                    .disabled(!FileManager.default.fileExists(atPath: database.path))
            }
            .buttonStyle(.link)
            .font(.caption)
        }
        .padding(.leading, indent)
    }

    /// Current problems and channels that are down. The row already says "Stopped", and a failed run
    /// is listed under Recent Issues with its time. Channels are only known to be down while the source runs.
    private var problems: [(text: String, severity: Severity)] {
        let own = zip(collector.problems, collector.problemSentences(now: now)).compactMap { problem, sentence -> (String, Severity)? in
            guard problem != "run_failed", problem != "not_loaded" else { return nil }
            return (sentence, problem == "not_running" ? .failure : .warning)
        }
        let channels = collector.severity == .ok ? lanes.compactMap(\.currentProblem) : []
        return own + channels.map { ($0, .warning) }
    }

    private var issues: [Issue] { Array(collector.recentIssues.prefix(5)) }

    private var store: URL { URL(fileURLWithPath: status.store, isDirectory: true) }

    private var database: URL {
        store.appendingPathComponent("observations", isDirectory: true)
            .appendingPathComponent(collector.name, isDirectory: true)
    }

    private var logs: [URL] {
        let directory = store.appendingPathComponent("logs", isDirectory: true)
        return ["err", "out"].map { directory.appendingPathComponent("\(collector.name).\($0).log") }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
    }

    private func openLogs() {
        for log in logs { NSWorkspace.shared.open(log) }
    }
}
