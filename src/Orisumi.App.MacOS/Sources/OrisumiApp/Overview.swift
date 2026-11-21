import AppKit
import SwiftUI

/// The whole window: what's going on, then today on one time axis with a row per source that
/// opens into its channels, issues and process facts.
struct Overview: View {
    let status: CaptureStatus
    let coverage: Coverage?
    @Binding var expanded: Set<String>
    let busy: Bool
    let start: () -> Void

    static let labelWidth: CGFloat = 190

    private var now: Date { status.generatedAt }
    private var day: DateInterval { Calendar.current.dateInterval(of: .day, for: now)! }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                header
                VStack(alignment: .leading, spacing: 8) {
                    Text("Today").font(.headline)
                    today
                }
                footer
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: - Header

    private var header: some View {
        HStack(alignment: .center, spacing: 14) {
            Image(systemName: status.severity.symbol)
                .font(.system(size: 30))
                .foregroundStyle(status.severity == .stopped ? AnyShapeStyle(.secondary) : AnyShapeStyle(status.severity.color))
                .symbolRenderingMode(.hierarchical)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(status.headline).font(.title2.weight(.semibold))
                if let explanation {
                    Text(explanation.text)
                        .foregroundStyle(explanation.warning ? AnyShapeStyle(Color.orange) : AnyShapeStyle(.secondary))
                        .lineLimit(2)
                        .textSelection(.enabled)
                }
            }
            Spacer(minLength: 16)
            if status.severity != .ok {
                Button(status.anyLoaded ? "Restart Capture" : "Start Capture", action: start)
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(busy)
            }
        }
        .accessibilityElement(children: .combine)
    }

    /// The first thing to know beyond the headline: the problem when one source has one,
    /// otherwise a channel that is down although its source is fine.
    private var explanation: (text: String, warning: Bool)? {
        let unhealthy = status.collectors.filter { $0.severity != .ok && $0.severity != .stopped }
        if unhealthy.count == 1 {
            return (unhealthy[0].problemSentences(now: now).joined(separator: " · "), false)
        }
        // A channel's last record only says something about now while its source runs.
        if unhealthy.isEmpty, let coverage {
            let down = status.collectors.filter { $0.severity == .ok }.flatMap { collector in
                coverage.channelLanes(for: collector.name, day: day, now: now).compactMap(\.currentProblem)
            }
            if down.count == 1 { return (down[0], true) }
            if down.count > 1 { return ("\(down.count) channels unavailable", true) }
        }
        return nil
    }

    // MARK: - Today

    private var today: some View {
        VStack(spacing: 0) {
            ForEach(Array(status.collectors.enumerated()), id: \.element.id) { index, collector in
                if index > 0 { Divider().padding(.leading, 30) }
                let lanes = coverage?.channelLanes(for: collector.name, day: day, now: now) ?? []
                SourceRow(collector: collector, status: status, day: day,
                          marks: coverage?.lane(for: collector.name, day: day, now: now),
                          downChannels: lanes.filter { $0.currentProblem != nil }.count,
                          expanded: expanded.contains(collector.id)) {
                    withAnimation(.snappy(duration: 0.2)) {
                        if expanded.contains(collector.id) { expanded.remove(collector.id) } else { expanded.insert(collector.id) }
                    }
                }
                if expanded.contains(collector.id) {
                    SourceDetail(collector: collector, status: status, day: day, lanes: lanes)
                        .padding(.bottom, 14)
                        .transition(.opacity)
                }
            }
            HStack(spacing: 12) {
                Color.clear.frame(width: Self.labelWidth, height: 1)
                HourAxis(day: day)
            }
            .padding(.top, 4)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(Card())
    }

    // MARK: - Footer

    private var footer: some View {
        HStack(spacing: 6) {
            Text([status.instance, status.hostVersion.map { "Orisumi \(shortVersion($0))" }, status.collectorVersionNote]
                .compactMap { $0 }.joined(separator: " · "))
                .textSelection(.enabled)
            Spacer()
            Button("Open Store Folder") {
                NSWorkspace.shared.open(URL(fileURLWithPath: status.store, isDirectory: true))
            }
            .buttonStyle(.link)
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }
}

/// A row in Today: the source's state on the left, its runs on the shared axis on the right.
private struct SourceRow: View {
    let collector: Collector
    let status: CaptureStatus
    let day: DateInterval
    let marks: [Mark]?
    let downChannels: Int
    let expanded: Bool
    let toggle: () -> Void

    private var now: Date { status.generatedAt }

    var body: some View {
        HStack(spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.tertiary)
                    .rotationEffect(.degrees(expanded ? 90 : 0))
                    .frame(width: 10)
                StatusDot(color: collector.severity.color)
                VStack(alignment: .leading, spacing: 1) {
                    Text(collector.displayName).fontWeight(.medium)
                    Text(caption)
                        .font(.caption)
                        .foregroundStyle(captionStyle)
                        .lineLimit(1)
                }
            }
            .frame(width: Overview.labelWidth, alignment: .leading)
            if let marks {
                Track(day: day, now: now, marks: marks)
            } else {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity)
            }
        }
        .padding(.vertical, 8)
        .contentShape(Rectangle())
        .onTapGesture(perform: toggle)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(collector.displayName)
        .accessibilityValue(caption)
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: expanded ? "Collapse" : "Expand", toggle)
    }

    private var caption: String {
        let summary = collector.summary(now: now)
        guard collector.severity == .ok, downChannels > 0 else { return summary }
        return downChannels == 1 ? "1 channel unavailable" : "\(downChannels) channels unavailable"
    }

    private var captionStyle: AnyShapeStyle {
        switch collector.severity {
        case .ok: downChannels > 0 ? AnyShapeStyle(Color.orange) : AnyShapeStyle(.secondary)
        case .stopped: AnyShapeStyle(.secondary)
        case .warning, .failure: AnyShapeStyle(collector.severity.color)
        }
    }
}

struct Card: View {
    var body: some View {
        RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(Color(nsColor: .controlBackgroundColor))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
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
