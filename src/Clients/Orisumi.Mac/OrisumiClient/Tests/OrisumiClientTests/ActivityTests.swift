import Foundation
import Testing
@testable import OrisumiClient

struct ActivityTests {
    static let day = DateInterval(start: Date(timeIntervalSince1970: 1_792_857_600), duration: 86_400)
    static let code = UUID(uuidString: "019e1bbb-ec00-7000-8000-0000000000c1")!
    static let codex = UUID(uuidString: "019e1bbb-ec00-7000-8000-0000000000c2")!

    static func period(_ id: Int, _ name: String, from start: TimeInterval, to end: TimeInterval,
                       application: UUID? = nil, icon: String? = nil) -> Period {
        Period(observationId: UUID(uuidString: String(format: "019e1bbb-ec00-7000-8000-%012d", id))!, name: name, applicationId: application,
               icon: icon, startAt: day.start + start, endAt: day.start + end)
    }

    @Test func clipsPeriodsToTheDay() {
        let yesterday = Self.period(1, "Safari", from: -7_200, to: -3_600)
        let acrossMidnight = Self.period(2, "Codex", from: -600, to: 600)
        #expect(yesterday.clipped(to: Self.day, now: Self.day.start + 700) == nil)
        #expect(acrossMidnight.clipped(to: Self.day, now: Self.day.start)?.duration == 600)
    }

    @Test func sumsUsagePerApplicationUpToTheLastConfirmation() {
        let periods = [
            Self.period(1, "Visual Studio Code", from: 0, to: 600, application: Self.code),
            Self.period(2, "Code", from: 1_000, to: 1_300, application: Self.code),
            Self.period(3, "Claude", from: 2_000, to: 2_010),
        ]
        let usage = Today.usage(periods, day: Self.day, now: Self.day.start + 3_000)
        // The active period counts until its last confirmation, not until now.
        #expect(usage.map(\.id) == [Self.code.uuidString, "Claude"])
        #expect(usage.map(\.duration) == [900, 10])
        #expect(usage.first?.periods == 2)
        #expect(usage.first?.longest == 600)
    }

    @Test func usageShowsTheIconOfTheLatestPeriodThatHasOne() {
        let periods = [
            Self.period(2, "Code", from: 1_000, to: 1_300, application: Self.code, icon: "new"),
            Self.period(1, "Code", from: 0, to: 600, application: Self.code, icon: "old"),
            Self.period(3, "Code", from: 2_000, to: 2_100, application: Self.code),
        ]
        #expect(Today.usage(periods, day: Self.day, now: Self.day.start + 3_000).first?.icon == "new")
    }
}

extension ActivityTests {
    @Test func drawsFromTheFirstActivityToNowOnWholeHours() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let periods = [Self.period(1, "Codex", from: 19 * 3_600 + 1_200, to: 20 * 3_600)]
        let range = Today.visibleRange(periods, day: Self.day, now: Self.day.start + 21 * 3_600 + 300, minimumHours: 3, calendar: calendar)
        #expect(range == DateInterval(start: Self.day.start + 19 * 3_600, end: Self.day.start + 22 * 3_600))
        #expect(Today.marks(in: range).count == 4)
        // A short morning still gets three hours, and a full day gets 6-hour marks.
        let morning = Today.visibleRange([], day: Self.day, now: Self.day.start + 600, minimumHours: 3, calendar: calendar)
        #expect(morning == DateInterval(start: Self.day.start, end: Self.day.start + 3 * 3_600))
        #expect(Today.marks(in: Self.day).count == 5)
    }
}

extension ActivityTests {
    @Test func splitsPeriodsIntoHoursAndGroupsTheRest() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let periods = [
            Self.period(1, "Codex", from: 3_000, to: 4_200, application: Self.codex),
            Self.period(2, "Safari", from: 4_200, to: 4_500),
            Self.period(3, "Notes", from: 4_500, to: 4_560),
        ]
        let slices = Today.hourly(periods, range: Self.day, now: Self.day.start + 5_000, keys: [Self.codex.uuidString], calendar: calendar)
        #expect(slices.map(\.duration) == [600, 600, 360])
        #expect(slices.map(\.key) == [Self.codex.uuidString, Self.codex.uuidString, nil])
        #expect(slices.map(\.hour) == [Self.day.start, Self.day.start + 3_600, Self.day.start + 3_600])
    }
}
