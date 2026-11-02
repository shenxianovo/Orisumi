import Foundation

/// The saved items read for a view.
public struct Snapshot<Item: Sendable>: Sendable {
    public var items: [Item]
    public init(items: [Item]) { self.items = items }
}

/// Reads all pages of one query.
@MainActor
func readAll<Content: Decodable & Sendable>(_ query: ReadQuery, from reader: DataReading,
                                            as: Content.Type) async throws -> [RuntimeObservation<Content>] {
    var query = query
    var items: [RuntimeObservation<Content>] = []
    repeat {
        let page = try await reader.read(query, as: Content.self)
        items += page.observations
        query.cursor = page.next
    } while query.cursor != nil
    return items
}

/// Today's foreground application periods of one instance of the foreground state Observer.
@MainActor
public final class ForegroundReader {
    public static let resourceName = "foreground-application-periods"

    public init() {}

    public func today(instanceId: UUID, day: DateInterval, from reader: DataReading) async throws -> Snapshot<Period> {
        let observations: [RuntimeObservation<PeriodContent>]
        do {
            observations = try await readAll(ReadQuery(instanceId: instanceId, during: day, resourceName: Self.resourceName),
                                             from: reader, as: PeriodContent.self)
        } catch let RuntimeError.remote(code, _) where code == "not_found" {
            // The Observer declares the type on its first write; until then there is nothing to read.
            return Snapshot(items: [])
        }
        let items = observations.flatMap { item -> [Period] in
            guard let startAt = item.startAt, let endAt = item.endAt else { return [] }
            return Today.remaining(from: startAt, to: endAt, denied: item.deniedRanges ?? []).enumerated().map { part, range in
                Period(observationId: item.observationId, part: part, name: item.content.name,
                       applicationId: item.content.localApplicationId, icon: item.content.icon?.digest,
                       startAt: range.start, endAt: range.end)
            }
        }
        return Snapshot(items: items)
    }

    struct PeriodContent: Decodable, Sendable {
        var name: String?
        var icon: Binary?
        var localApplicationId: UUID?
    }

    struct Binary: Decodable, Sendable {
        var digest: String
    }
}

/// A recent Observation of any Observer, with its content as plain JSON.
public struct RecentObservation: Equatable, Identifiable, Sendable {
    public var id: UUID
    public var startAt: Date?
    public var endAt: Date?
    public var content: JSONValue?
}

/// The latest Observations of one instance, for Observers without a dedicated view.
@MainActor
public final class RecentReader {
    public init() {}

    public func recent(instanceId: UUID, now: Date, days: Int = 7, limit: Int = 50,
                       from reader: DataReading) async throws -> Snapshot<RecentObservation> {
        let interval = DateInterval(start: now.addingTimeInterval(-Double(days) * 86_400), end: now)
        let page = try await reader.read(ReadQuery(instanceId: instanceId, during: interval, limit: limit, descending: true),
                                         as: JSONValue.self)
        let items = page.observations.map { item in
            RecentObservation(id: item.observationId, startAt: item.startAt, endAt: item.endAt, content: item.content)
        }
        return Snapshot(items: items)
    }
}
