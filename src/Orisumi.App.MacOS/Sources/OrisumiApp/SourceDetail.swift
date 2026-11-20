import AppKit
import SwiftUI

struct SourceDetail: View {
    let collector: Collector
    let status: CaptureStatus
    let channels: [Channel]
    let store: URL

    private var now: Date { status.generatedAt }

    var body: some View {
        Form {
            Section {
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(collector.displayName).font(.title2.weight(.semibold))
                        HStack(spacing: 6) {
                            StatusDot(color: collector.stateColor)
                            Text(collector.sentence(now: now)).foregroundStyle(.secondary)
                        }
                    }
                    Spacer(minLength: 12)
                    Button("Show Log", systemImage: "doc.text", action: openLogs)
                        .disabled(logs.isEmpty)
                        .help(logs.isEmpty ? "No log files yet" : logs.map(\.lastPathComponent).joined(separator: ", "))
                    Button("Reveal Database", systemImage: "folder") {
                        NSWorkspace.shared.activateFileViewerSelecting([database])
                    }
                    .disabled(!FileManager.default.fileExists(atPath: database.path))
                }
                .padding(.vertical, 4)
            }

            if collector.problems.count > 1 {
                Section("Needs Attention") {
                    ForEach(Array(zip(collector.problems, collector.problemSentences(now: now))), id: \.0) { problem, sentence in
                        let severe = problem == "not_running" || problem == "run_failed"
                        Label {
                            Text(sentence).textSelection(.enabled)
                        } icon: {
                            Image(systemName: severe ? "xmark.octagon.fill" : "exclamationmark.triangle.fill")
                                .foregroundStyle(severe ? Color.red : Color.orange)
                        }
                    }
                }
            }

            Section("Status") {
                LabeledContent("Agent") { Text(collector.agentText).textSelection(.enabled) }
                LabeledContent("Version") {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(shortVersion(collector.version)).textSelection(.enabled).help(collector.version ?? "")
                        if status.versionDiffers(collector) {
                            Text("Host runs \(shortVersion(status.hostVersion))").font(.caption).foregroundStyle(.orange)
                        }
                    }
                }
                LabeledContent("Current run") { Text(currentRun) }
                if let confirmed = collector.run?.confirmed, collector.run?.isOpen == true {
                    LabeledContent("Last confirmed") {
                        Text(relativeDate(confirmed, to: now)).help(absoluteDate(confirmed))
                    }
                }
                LabeledContent("Read failures in the last hour") {
                    Text((collector.readFailuresLastHour ?? 0).formatted())
                }
            }

            Section("Channels") {
                if channels.isEmpty {
                    Text("No channel changes recorded today").foregroundStyle(.secondary)
                } else {
                    ForEach(channels) { ChannelRow(channel: $0) }
                }
            }

            Section("Recent Runs") {
                if collector.recentRuns.isEmpty {
                    Text("No runs yet").foregroundStyle(.secondary)
                } else {
                    ForEach(collector.recentRuns) { RunRow(run: $0, now: now) }
                }
            }

            Section("Recent Read Failures") {
                if collector.recentReadFailures.isEmpty {
                    Text("No read failures").foregroundStyle(.secondary)
                } else {
                    ForEach(Array(collector.recentReadFailures.enumerated()), id: \.offset) { _, failure in
                        FailureRow(failure: failure, now: now)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var currentRun: String {
        guard let run = collector.run else { return "No runs yet" }
        if run.isOpen { return "Started \(timeText(run.started, now: now)) · \(run.durationText(now: now))" }
        let reason = run.stopReason.map(humanized) ?? "Ended"
        return "None · last ended \(timeText(run.stopped!, now: now)) (\(reason.lowercased()))"
    }

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

private struct ChannelRow: View {
    let channel: Channel

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: symbol)
                .foregroundStyle(color)
                .frame(width: 18)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(channel.name)
                Text(channel.method).font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            Text(trailing)
                .foregroundStyle(channel.unavailable ? AnyShapeStyle(Color.orange) : AnyShapeStyle(.secondary))
                .multilineTextAlignment(.trailing)
                .textSelection(.enabled)
        }
        .accessibilityElement(children: .combine)
    }

    private var available: Bool { channel.status == "Available" }
    private var symbol: String {
        channel.unavailable ? "exclamationmark.triangle.fill" : available ? "checkmark.circle.fill" : "minus.circle"
    }
    private var color: Color { channel.unavailable ? .orange : available ? .green : .secondary }
    private var trailing: String {
        if let detail = channel.detail { return channel.unavailable || available ? detail : "\(channel.status) · \(detail)" }
        return channel.status
    }
}

private struct RunRow: View {
    let run: Run
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline) {
                Text(run.spanText(now: now)).monospacedDigit()
                Text(run.durationText(now: now)).foregroundStyle(.secondary)
                Spacer(minLength: 12)
                Text(state).foregroundStyle(stateStyle)
            }
            if let error = run.error {
                Text(error).font(.callout).foregroundStyle(.red).textSelection(.enabled)
            }
        }
        .help("Run \(run.runId) · \(run.collectorVersion ?? "unknown version")")
        .accessibilityElement(children: .combine)
    }

    private var state: String {
        run.isOpen ? "Running" : run.stopReason.map(humanized) ?? "Ended"
    }

    private var stateStyle: AnyShapeStyle {
        if run.isOpen { return AnyShapeStyle(Color.accentColor) }
        return run.stopReason == "failed" ? AnyShapeStyle(Color.red) : AnyShapeStyle(.secondary)
    }
}

private struct FailureRow: View {
    let failure: ReadFailure
    let now: Date

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(timeText(failure.observed, now: now))
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .frame(minWidth: 64, alignment: .leading)
                .help(absoluteDate(failure.observed))
            VStack(alignment: .leading, spacing: 2) {
                Text(failure.error)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
                if let scope = failure.scope {
                    Text(humanized(scope)).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }
}
