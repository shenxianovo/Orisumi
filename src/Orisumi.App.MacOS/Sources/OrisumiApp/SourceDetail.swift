import AppKit
import SwiftUI

struct SourceDetail: View {
    let collector: Collector
    let channels: [Channel]
    let store: URL
    let now: Date
    @State private var selectedFailure: Int?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .firstTextBaseline) {
                    Text(collector.displayName).font(.largeTitle.bold())
                    Spacer()
                    Button("Show Log", systemImage: "doc.text", action: openLogs)
                        .disabled(logs.isEmpty)
                    Button("Reveal Database", systemImage: "folder") {
                        NSWorkspace.shared.activateFileViewerSelecting([database])
                    }
                    .disabled(!FileManager.default.fileExists(atPath: database.path))
                }
                section("Status") { statusGrid }
                section("Channels") { channelsTable }
                section("Recent runs") { runsTable }
                section("Recent read failures") { failuresTable }
            }
            .padding(24)
        }
    }

    private var statusGrid: some View {
        Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 8) {
            GridRow {
                field("State")
                VStack(alignment: .leading, spacing: 4) {
                    Label {
                        Text(collector.health.title)
                    } icon: {
                        Image(systemName: "circle.fill").font(.system(size: 7))
                            .foregroundStyle(collector.stateColor)
                    }
                    if !collector.problemLabels.isEmpty {
                        Text(collector.problemLabels.joined(separator: " · ")).foregroundStyle(.secondary)
                    }
                }
            }
            GridRow { field("Agent"); Text(collector.agent?.displayState ?? "—") }
            GridRow {
                field("Version")
                Text(shortVersion(collector.version)).help(collector.version ?? "").textSelection(.enabled)
            }
            GridRow { field("Current run start"); Text(absoluteDate(collector.run?.started)) }
            GridRow {
                field("Last confirmed")
                if let confirmed = collector.run?.confirmed {
                    Text("\(relativeDate(confirmed, to: now)) · \(absoluteDate(confirmed))")
                } else {
                    Text("—")
                }
            }
        }
    }

    @ViewBuilder
    private var channelsTable: some View {
        if channels.isEmpty {
            empty("No channel evidence for today")
        } else {
            Table(channels) {
                TableColumn("Channel", value: \.name)
                TableColumn("Status") { channel in
                    Text(channel.status).foregroundStyle(channel.unavailable ? Color.orange : Color.primary)
                }
                TableColumn("Method") { Text(words($0.method).capitalized) }
                TableColumn("Detail") { Text($0.detail ?? "—").help($0.detail ?? "") }
            }
            .frame(height: tableHeight(channels.count))
        }
    }

    @ViewBuilder
    private var runsTable: some View {
        let runs = collector.recentRuns
        if !runs.isEmpty {
            Table(runs) {
                TableColumn("Started") { tableDate($0.started).help(absoluteDate($0.started)) }
                TableColumn("Ended") { tableDate($0.stopped).help(absoluteDate($0.stopped)) }
                TableColumn("Duration") { run in
                    Text(duration(run)).monospacedDigit()
                }
                .width(72)
                TableColumn("Stop reason") { Text($0.stopReason.map { words($0).capitalized } ?? "—") }
                TableColumn("Error") { Text($0.error ?? "—").help($0.error ?? "") }
                TableColumn("Version") { Text(shortVersion($0.collectorVersion)).monospacedDigit() }
            }
            .frame(height: tableHeight(runs.count))
        } else {
            empty("No recent runs")
        }
    }

    @ViewBuilder
    private var failuresTable: some View {
        let failures = collector.recentReadFailures
        if !failures.isEmpty {
            Table(Array(failures.enumerated()).map(FailureRow.init), selection: $selectedFailure) {
                TableColumn("Time") { tableDate($0.failure.observed).help(absoluteDate($0.failure.observed)) }
                    .width(min: 100, ideal: 115, max: 170)
                TableColumn("Scope") { Text($0.failure.scope ?? "—") }
                    .width(min: 90, ideal: 100, max: 140)
                TableColumn("Error") { Text($0.failure.error).textSelection(.enabled).help($0.failure.error) }
            }
            .frame(height: tableHeight(failures.count))
            if let selectedFailure, failures.indices.contains(selectedFailure) {
                Text(failures[selectedFailure].error)
                    .textSelection(.enabled)
                    .foregroundStyle(.secondary)
            }
        } else {
            empty("No recent read failures")
        }
    }

    private func field(_ title: String) -> some View { Text(title).foregroundStyle(.secondary) }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.title2.bold())
            content()
        }
    }

    private func empty(_ message: String) -> some View { Text(message).foregroundStyle(.secondary) }

    private func tableHeight(_ rows: Int) -> CGFloat { CGFloat(min(rows, 6)) * 25 + 30 }

    private func duration(_ run: Run) -> String {
        guard let end = run.stopped ?? run.confirmed, end >= run.started else { return "—" }
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.day, .hour, .minute, .second]
        formatter.unitsStyle = .abbreviated
        formatter.maximumUnitCount = 2
        return formatter.string(from: run.started, to: end) ?? "—"
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

private struct FailureRow: Identifiable {
    let id: Int
    let failure: ReadFailure

    init(_ entry: (Int, ReadFailure)) {
        (id, failure) = entry
    }
}
