import Foundation

struct ActivityIdleTracker {
    let threshold: TimeInterval
    private(set) var state: AFKState?
    private var lastActivity: TimeInterval?

    mutating func reset() {
        state = nil
        lastActivity = nil
    }

    mutating func observe(idle: TimeInterval, at uptime: TimeInterval) -> (AFKEventKind, AFKState, Int64)? {
        lastActivity = uptime - idle
        let next: AFKState = idle >= threshold ? .idle : .active
        guard next != state else { return nil }
        let kind: AFKEventKind = state == nil ? .snapshot : .transition
        state = next
        return (kind, next, Int64(idle * 1000))
    }

    mutating func activity(at uptime: TimeInterval) -> (AFKEventKind, AFKState, Int64)? {
        observe(idle: 0, at: uptime)
    }

    mutating func tick(at uptime: TimeInterval) -> (AFKEventKind, AFKState, Int64)? {
        guard let lastActivity else { return nil }
        return observe(idle: max(0, uptime - lastActivity), at: uptime)
    }

    func remaining(at uptime: TimeInterval) -> TimeInterval? {
        guard state == .active, let lastActivity else { return nil }
        return max(0, lastActivity + threshold - uptime)
    }
}
