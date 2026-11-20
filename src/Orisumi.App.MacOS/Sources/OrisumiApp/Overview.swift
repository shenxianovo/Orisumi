import AppKit
import SwiftUI

struct Overview: View {
    let status: CaptureStatus
    let coverage: Coverage?
    let selectSource: (String) -> Void

    private var unhealthy: [Collector] { status.collectors.filter { $0.health != .ok } }

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    AppIconView(size: 56)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(status.headline).font(.title2.weight(.semibold))
                        TimelineView(.periodic(from: .now, by: 1)) { context in
                            Text(subtitle(now: context.date)).foregroundStyle(.secondary)
                        }
                    }
                }
                .padding(.vertical, 4)
            }

            if !unhealthy.isEmpty {
                Section("Needs Attention") {
                    ForEach(unhealthy) { collector in
                        ProblemRow(collector: collector, now: status.generatedAt) { selectSource(collector.id) }
                    }
                }
            }

            Section("Today") {
                if let coverage {
                    DayTimeline(collectors: status.collectors, coverage: coverage, now: status.generatedAt)
                        .padding(.vertical, 6)
                } else {
                    ProgressView().controlSize(.small).frame(maxWidth: .infinity, minHeight: 60)
                        .accessibilityLabel("Loading today's runs")
                }
            }

            Section("Sources") {
                ForEach(status.collectors) { collector in
                    SourceRow(collector: collector, status: status) { selectSource(collector.id) }
                }
            }
        }
        .formStyle(.grouped)
    }

    private func subtitle(now: Date) -> String {
        var parts = [status.instance]
        if let host = status.hostVersion { parts.append("Orisumi \(shortVersion(host))") }
        parts.append("updated \(relativeDate(status.generatedAt, to: now))")
        return parts.joined(separator: " · ")
    }
}

struct AppIconView: View {
    let size: CGFloat

    var body: some View {
        Image(nsImage: NSApp.applicationIconImage)
            .resizable()
            .interpolation(.high)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

struct StatusDot: View {
    let color: Color

    var body: some View {
        Circle().fill(color).frame(width: 8, height: 8).accessibilityHidden(true)
    }
}

private struct ProblemRow: View {
    let collector: Collector
    let now: Date
    let show: () -> Void

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Image(systemName: collector.stateColor == .red ? "xmark.octagon.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(collector.stateColor == .red ? Color.red : collector.health == .stopped ? Color.secondary : Color.yellow)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(collector.displayName).fontWeight(.medium)
                ForEach(collector.problemSentences(now: now), id: \.self) { problem in
                    Text(problem).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }
            Spacer(minLength: 12)
            Button("Show", action: show).controlSize(.small)
        }
        .padding(.vertical, 2)
    }
}

private struct SourceRow: View {
    let collector: Collector
    let status: CaptureStatus
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            HStack(spacing: 10) {
                StatusDot(color: collector.stateColor)
                VStack(alignment: .leading, spacing: 2) {
                    Text(collector.displayName)
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(collector.sentence(now: max(context.date, status.generatedAt)))
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    if status.versionDiffers(collector) {
                        Text("Version \(shortVersion(collector.version)) differs from the host")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
                Spacer(minLength: 12)
                if let failures = collector.readFailuresLastHour, failures > 0 {
                    Text(failures == 1 ? "1 read failure in the last hour" : "\(failures) read failures in the last hour")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(.tertiary)
            }
            .padding(.vertical, 2)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

/// Today's runs per source on a midnight-to-midnight axis, so the gaps and the time of day read at a glance.
private struct DayTimeline: View {
    let collectors: [Collector]
    let coverage: Coverage
    let now: Date

    private let labelWidth: CGFloat = 92
    private let rowHeight: CGFloat = 24

    var body: some View {
        let day = Calendar.current.dateInterval(of: .day, for: now)!
        let spans = coverage.runs(in: day.start..<now)
        VStack(spacing: 0) {
            ForEach(collectors) { collector in
                HStack(spacing: 12) {
                    Text(collector.displayName)
                        .lineLimit(1)
                        .frame(width: labelWidth, alignment: .leading)
                    Track(day: day, now: now, spans: spans.filter { $0.source == collector.name })
                }
                .frame(height: rowHeight)
                .accessibilityElement(children: .combine)
                .accessibilityLabel(collector.displayName)
                .accessibilityValue(accessibilitySummary(spans.filter { $0.source == collector.name }))
            }
            HStack(spacing: 12) {
                Color.clear.frame(width: labelWidth, height: 1)
                HourAxis(day: day)
            }
            .accessibilityHidden(true)
        }
    }

    private func accessibilitySummary(_ spans: [RunSpan]) -> String {
        guard !spans.isEmpty else { return "No runs today" }
        return spans.map { "\(clockText($0.start)) to \($0.row.stopped == nil ? "now" : clockText($0.end))" }
            .joined(separator: ", ")
    }

    private struct Track: View {
        let day: DateInterval
        let now: Date
        let spans: [RunSpan]

        var body: some View {
            GeometryReader { geometry in
                let width = geometry.size.width
                let x = { (date: Date) in CGFloat(date.timeIntervalSince(day.start) / day.duration) * width }
                ZStack(alignment: .leading) {
                    ForEach(0...8, id: \.self) { step in
                        Rectangle().fill(.quaternary)
                            .frame(width: 1)
                            .offset(x: min(width - 1, CGFloat(step) / 8 * width))
                    }
                    // The rest of the day hasn't happened yet.
                    Rectangle().fill(.quinary)
                        .frame(width: max(0, width - x(now)), height: 10)
                        .clipShape(.rect(cornerRadius: 3))
                        .offset(x: x(now))
                    ForEach(spans) { span in
                        RoundedRectangle(cornerRadius: 3)
                            .fill(span.row.evidence == "failed" ? Color.red : Color.accentColor)
                            .frame(width: max(2, x(end(of: span)) - x(span.start)), height: 10)
                            .offset(x: x(span.start))
                            .help(tooltip(span))
                    }
                    Rectangle().fill(.secondary)
                        .frame(width: 1.5, height: 18)
                        .offset(x: x(now) - 0.75)
                        .help("Now · \(clockText(now))")
                }
                .frame(maxHeight: .infinity)
            }
        }

        /// Coverage is read once a minute; a run confirmed within the stale limit is drawn up to now.
        private func end(of span: RunSpan) -> Date {
            guard span.row.stopped == nil, now.timeIntervalSince(span.end) < 300 else { return span.end }
            return now
        }

        private func tooltip(_ span: RunSpan) -> String {
            let end = span.row.stopped.map(clockText) ?? "now"
            var text = "\(clockText(span.row.started)) – \(end) · \(elapsedText(from: span.row.started, to: span.row.stopped ?? now))"
            switch span.row.evidence {
            case "completed": text += "\nCompleted"
            case "failed": text += "\nFailed" + (span.row.detail.map { ": \($0)" } ?? "")
            default: if let confirmed = span.row.confirmed { text += "\nConfirmed \(relativeDate(confirmed, to: now))" }
            }
            return text
        }
    }

    private struct HourAxis: View {
        let day: DateInterval

        var body: some View {
            GeometryReader { geometry in
                ForEach([0, 6, 12, 18, 24], id: \.self) { hour in
                    let date = day.start.addingTimeInterval(Double(hour) * 3600)
                    Text(hour == 24 ? "" : date.formatted(.dateTime.hour().locale(appLocale)))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        .position(x: CGFloat(hour) / 24 * geometry.size.width, y: 8)
                }
            }
            .frame(height: 16)
        }
    }
}
