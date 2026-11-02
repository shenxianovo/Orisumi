import Foundation
import Testing
@testable import OrisumiClient

/// Serves pages from a table.
@MainActor
final class FakeReader: DataReading {
    var pages: [String?: String] = [:]
    var queries: [ReadQuery] = []
    var failure: RuntimeError?

    func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws -> ReadResult<Content> {
        queries.append(query)
        if let failure { throw failure }
        return try RuntimeJSON.decoder.decode(ReadResult<Content>.self, from: Data(pages[query.cursor]!.utf8))
    }
}

@MainActor
struct ObservationReaderTests {
    static let instance = UUID(uuidString: "019e1bbb-ec00-7000-8000-0000000000f1")!
    static let application = UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000301")!

    static func observation(_ index: Int, _ content: String, endAt: String? = "2026-10-24T16:50:00.5Z",
                            denied: String = "") -> String {
        let end = endAt.map { "\"\($0)\"" } ?? "null"
        return #"{"observationId":"019e1bbb-ec00-7000-8000-00000000010\#(index)","observerId":"\#(instance.uuidString)","formation":"direct","startAt":"2026-10-24T16:00:00Z","endAt":\#(end),\#(denied)"content":\#(content)}"#
    }

    static func reader() -> FakeReader {
        let reader = FakeReader()
        let codex = #"{"name":"Codex","localApplicationId":"\#(application.uuidString)"}"#
        let safari = #"{"name":"Safari","icon":{"digest":"sha256:abababababababababababababababababababababababababababababababab","mediaType":"image/png","size":3},"localApplicationId":null}"#
        reader.pages = [
            nil: #"{"observations":[\#(observation(1, codex))],"next":"page-2"}"#,
            "page-2": #"{"observations":[\#(observation(2, safari)),\#(observation(3, safari, endAt: nil))],"next":null}"#,
        ]
        return reader
    }

    @Test func readsTodaysPeriodsAcrossPages() async throws {
        let reader = Self.reader()
        let today = try await ForegroundReader().today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        // A period without a known end is left out.
        #expect(today.items.map(\.name) == ["Codex", "Safari"])
        #expect(today.items.first?.applicationId == Self.application)
        #expect(today.items.map(\.icon) == [nil, "sha256:abababababababababababababababababababababababababababababababab"])
        #expect(today.items.first?.endAt == RuntimeJSON.parseDate("2026-10-24T16:50:00.5Z"))

        #expect(reader.queries.first?.resourceName == ForegroundReader.resourceName)
        #expect(reader.queries.first?.instanceId == Self.instance)
    }

    @Test func cutsDeniedRangesOutOfAPeriod() async throws {
        let reader = FakeReader()
        let denied = #""deniedRanges":[{"startAt":"2026-10-24T16:10:00Z","endAt":"2026-10-24T16:20:00Z"}],"#
        reader.pages = [nil: #"{"observations":[\#(Self.observation(1, "{\"name\":\"Codex\"}", denied: denied))],"next":null}"#]
        let today = try await ForegroundReader().today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(today.items.map(\.startAt) == [RuntimeJSON.parseDate("2026-10-24T16:00:00Z"), RuntimeJSON.parseDate("2026-10-24T16:20:00Z")])
        #expect(today.items.first?.endAt == RuntimeJSON.parseDate("2026-10-24T16:10:00Z"))
        #expect(Set(today.items.map(\.id)).count == 2)
    }

    @Test func aTypeNotDeclaredYetReadsAsNoPeriods() async throws {
        let reader = FakeReader()
        reader.failure = .remote(code: "not_found", message: "No type uses this resourceName.")
        let today = try await ForegroundReader().today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(today.items.isEmpty)
    }

    @Test func encodesTheObservationQuery() {
        let day = DateInterval(start: Date(timeIntervalSince1970: 0), duration: 3600)
        let path = ReadQuery(instanceId: Self.instance, during: day, resourceName: "periods", descending: true, cursor: "a+b=").path
        #expect(path.hasPrefix("observations?observerId=\(Self.instance.uuidString.lowercased())&from="))
        #expect(!path.dropFirst("observations?".count).contains("+"))
        #expect(path.hasSuffix("&limit=200&resourceName=periods&order=desc&after=a%2Bb%3D"))
    }

    @Test func readsRecentObservationsWithPlainContent() async throws {
        let reader = Self.reader()
        let recent = try await RecentReader().recent(instanceId: Self.instance, now: .now, from: reader)
        #expect(recent.items.count == 1)
        #expect(recent.items.first?.content?.compact == #"{"localApplicationId":"\#(Self.application.uuidString)","name":"Codex"}"#)
    }
}
