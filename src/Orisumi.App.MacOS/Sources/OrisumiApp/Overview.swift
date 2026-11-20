import Charts
import SwiftUI

struct Overview: View {
    let status: CaptureStatus
    let coverage: Coverage?
    let selectSource: (String) -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                VStack(alignment: .leading, spacing: 5) {
                    Label(status.health.title, systemImage: status.health.symbol)
                        .font(.largeTitle.bold())
                        .symbolRenderingMode(.hierarchical)
                    HStack(spacing: 12) {
                        Text(status.instance)
                        if let version = status.hostVersion { Text("Host \(shortVersion(version))").help(version) }
                    }
                    .foregroundStyle(.secondary)
                }
                VStack(alignment: .leading, spacing: 12) {
                    Text("Today").font(.title2.bold())
                    if let coverage {
                        TimelineView(.periodic(from: .now, by: 5)) { context in
                            CoverageChart(collectors: status.collectors, coverage: coverage, now: context.date)
                        }
                    } else {
                        ProgressView().controlSize(.small).accessibilityLabel("Loading coverage")
                            .frame(maxWidth: .infinity, minHeight: 110)
                    }
                }
                VStack(alignment: .leading, spacing: 8) {
                    Text("Sources").font(.title2.bold())
                    sourcesTable
                }
            }
            .padding(24)
        }
    }

    private var sourcesTable: some View {
        Table(status.collectors, selection: Binding<String?>(
            get: { nil },
            set: { if let source = $0 { selectSource(source) } }
        )) {
            TableColumn("Source") { Text($0.displayName) }
            TableColumn("State") { collector in
                Label {
                    Text(collector.stateLabel)
                } icon: {
                    Image(systemName: "circle.fill").font(.system(size: 7))
                        .foregroundStyle(collector.stateColor)
                }
                .help(collector.stateLabel)
            }
            TableColumn("Version") { Text(shortVersion($0.version)).monospacedDigit() }
            TableColumn("Started") { tableDate($0.run?.started).help(absoluteDate($0.run?.started)) }
            TableColumn("Last confirmed") { collector in
                if let confirmed = collector.run?.confirmed { Text(relativeDate(confirmed, to: status.generatedAt)).help(absoluteDate(confirmed)) }
                else { Text("—") }
            }
            TableColumn("Failures (1 h)") { Text($0.readFailuresLastHour?.formatted() ?? "—") }
                .width(85)
        }
        .frame(height: CGFloat(status.collectors.count) * 25 + 30)
    }
}

private struct CoverageChart: View {
    let collectors: [Collector]
    let coverage: Coverage
    let now: Date
    @State private var hovered: RunSpan?

    var body: some View {
        let start = Calendar.current.startOfDay(for: now)
        let end = max(now, start.addingTimeInterval(0.001))
        let interval = start..<end
        let spans = coverage.runs(in: interval)
        let names = collectors.map(\.name)
        Chart {
            ForEach(spans.filter { names.contains($0.source) }) { span in
                BarMark(xStart: .value("Start", span.start), xEnd: .value("End", span.end),
                        y: .value("Source", span.source), height: .fixed(12))
                    .foregroundStyle(Color.accentColor)
                    .clipShape(.rect(cornerRadius: 2))
                    .accessibilityLabel(Text(collectors.first { $0.name == span.source }?.displayName ?? span.source))
                    .accessibilityValue(Text("\(absoluteDate(span.row.started)) to \(absoluteDate(span.row.stopped ?? span.row.confirmed))"))
            }
        }
        .chartXScale(domain: start...end)
        .chartYScale(domain: names)
        .chartXAxis {
            AxisMarks(values: .automatic(desiredCount: 5)) {
                AxisGridLine()
                AxisValueLabel(format: .dateTime.hour().minute())
            }
        }
        .chartYAxis {
            AxisMarks(position: .leading, values: names) { value in
                AxisGridLine()
                AxisValueLabel {
                    if let name = value.as(String.self) {
                        Text(collectors.first { $0.name == name }?.displayName ?? name)
                    }
                }
            }
        }
        .chartOverlay { proxy in
            GeometryReader { geometry in
                Color.clear.contentShape(Rectangle())
                    .onContinuousHover { phase in
                        guard case .active(let point) = phase, let plot = proxy.plotFrame,
                              geometry[plot].contains(point),
                              let (time, source) = proxy.value(at: CGPoint(x: point.x - geometry[plot].minX,
                                                                           y: point.y - geometry[plot].minY),
                                                              as: (Date, String).self) else {
                            hovered = nil
                            return
                        }
                        hovered = spans.last { $0.source == source && $0.start <= time && time <= $0.end }
                    }
            }
        }
        .overlay(alignment: .topTrailing) {
            if let span = hovered {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Started \(absoluteDate(span.row.started))")
                    if let stopped = span.row.stopped {
                        Text("Ended \(absoluteDate(stopped))")
                        Text(words(span.row.evidence).capitalized)
                    } else if let confirmed = span.row.confirmed {
                        Text("Confirmed until \(absoluteDate(confirmed))")
                    } else {
                        Text("End unconfirmed")
                    }
                }
                .font(.caption)
                .padding(8)
                .background(.regularMaterial, in: .rect(cornerRadius: 6))
                .allowsHitTesting(false)
            }
        }
        .frame(height: CGFloat(collectors.count) * 29 + 25)
        .accessibilityLabel("Today's capture runs")
    }
}
