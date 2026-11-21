import SwiftUI

/// Midnight-to-midnight lane: what ran in the solid tint, gaps in orange, failures as red ticks.
/// The part of the day that hasn't happened yet stays empty.
struct Track: View {
    let day: DateInterval
    let now: Date
    let marks: [Mark]
    var height: CGFloat = 10
    var tint: Color = .accentColor

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            let middle = geometry.size.height / 2
            let x = { (date: Date) in CGFloat(date.timeIntervalSince(day.start) / day.duration) * width }
            ZStack(alignment: .topLeading) {
                ForEach(1..<4, id: \.self) { quarter in
                    Rectangle().fill(.quinary)
                        .frame(width: 1, height: geometry.size.height)
                        .offset(x: CGFloat(quarter) / 4 * width)
                }
                Capsule().fill(.quinary)
                    .frame(width: max(0, x(now)), height: height)
                    .offset(y: middle - height / 2)
                ForEach(marks.sorted { order($0.kind) < order($1.kind) }) { mark in
                    let left = x(mark.start)
                    let span = x(mark.end) - left
                    if mark.kind == .on || span >= 3 {
                        Capsule().fill(color(mark.kind))
                            .frame(width: max(height / 2, span), height: height)
                            .offset(x: left, y: middle - height / 2)
                            .help(mark.help)
                    } else {
                        // An instant: a tick taller than the bar, so it shows against any background.
                        Capsule().fill(color(mark.kind))
                            .frame(width: 2.5, height: height + 6)
                            .offset(x: left - 1.25, y: middle - (height + 6) / 2)
                            .help(mark.help)
                    }
                }
                Rectangle().fill(.secondary)
                    .frame(width: 1.5, height: geometry.size.height)
                    .offset(x: x(now) - 0.75)
                    .help("Now · \(clockText(now))")
            }
        }
        .frame(height: height + 10)
    }

    private func color(_ kind: Mark.Kind) -> Color {
        switch kind {
        case .on: tint
        case .warning: .orange
        case .failure: .red
        }
    }

    private func order(_ kind: Mark.Kind) -> Int {
        switch kind {
        case .on: 0
        case .warning: 1
        case .failure: 2
        }
    }
}

struct HourAxis: View {
    let day: DateInterval

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .topLeading) {
                ForEach([0, 6, 12, 18], id: \.self) { hour in
                    let date = day.start.addingTimeInterval(Double(hour) * 3600)
                    let x = CGFloat(hour) / 24 * geometry.size.width
                    Text(date.formatted(.dateTime.hour().locale(appLocale)))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        // Midnight starts at the edge; the others are centred on their line.
                        .alignmentGuide(.leading) { label in -x + (hour == 0 ? 0 : label.width / 2) }
                }
            }
        }
        .frame(height: 14)
        .accessibilityHidden(true)
    }
}
