import AppKit
import CoreGraphics

enum ScreenState: String {
    case lock = "screen_lock"
    case displaySleep = "display_sleep"
}

struct ScreenStateSource {
    let read: () -> Bool?
    let watch: (@escaping (Bool) -> Void) -> Cancel

    static func lock() -> Self {
        let center = DistributedNotificationCenter.default()
        return Self(read: {
            lockState(CGSessionCopyCurrentDictionary() as? [String: Any])
        }, watch: { changed in
            let observer = ScreenLockNotifications(changed: changed)
            center.addObserver(observer, selector: #selector(ScreenLockNotifications.locked), name: .init("com.apple.screenIsLocked"),
                               object: nil, suspensionBehavior: .deliverImmediately)
            center.addObserver(observer, selector: #selector(ScreenLockNotifications.unlocked), name: .init("com.apple.screenIsUnlocked"),
                               object: nil, suspensionBehavior: .deliverImmediately)
            return { center.removeObserver(observer) }
        })
    }

    static func lockState(_ session: [String: Any]?) -> Bool? {
        // The private key is present only while locked (checked on macOS 27): a readable session without it is unlocked.
        guard let session else { return nil }
        guard let raw = session["CGSSessionScreenIsLocked"] else { return false }
        guard let value = raw as? NSNumber else { return nil }
        if value == 0 { return false }
        if value == 1 { return true }
        return nil
    }

    static func displaySleep(_ workspace: NSWorkspace) -> Self {
        let center = workspace.notificationCenter
        return Self(read: {
            var count: UInt32 = 0
            guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else { return nil }
            var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
            var read: UInt32 = 0
            guard CGGetOnlineDisplayList(count, &displays, &read) == .success, read == count,
                  CGGetOnlineDisplayList(0, nil, &count) == .success, read == count,
                  displays.allSatisfy({ $0 != 0 && CGDisplayIsOnline($0) != 0 }) else { return nil }
            return displays.allSatisfy { CGDisplayIsAsleep($0) != 0 }
        }, watch: { changed in
            let slept = center.addObserver(forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) { _ in changed(true) }
            let woke = center.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { _ in changed(false) }
            return { center.removeObserver(slept); center.removeObserver(woke) }
        })
    }
}

private final class ScreenLockNotifications: NSObject {
    let changed: (Bool) -> Void
    init(changed: @escaping (Bool) -> Void) { self.changed = changed }

    @objc func locked(_ notification: Notification) { changed(true) }
    @objc func unlocked(_ notification: Notification) { changed(false) }
}

// Screen notifications are independent evidence. Keep observing them even while
// system sleep or a session change interrupts the other channels.
final class ScreenStateObserver {
    private let state: ScreenState
    private let source: ScreenStateSource
    private let events: ObservationEvents
    private let failed: (Error) -> Void
    private var last: Bool?
    private var cancel: Cancel?

    init(_ state: ScreenState, source: ScreenStateSource, events: ObservationEvents, failed: @escaping (Error) -> Void) {
        self.state = state
        self.source = source
        self.events = events
        self.failed = failed
    }

    func start() throws {
        cancel = source.watch { [weak self] value in
            guard let self, value != self.last else { return }
            do {
                try self.events.appendScreenState(self.state, value: value, snapshot: false, at: Date())
                self.last = value
            } catch { self.failed(error) }
        }
        try snapshot()
    }

    func snapshot() throws {
        last = source.read()
        try events.appendScreenState(state, value: last, snapshot: true, at: Date())
    }

    func stop() {
        cancel?()
        cancel = nil
    }
}
