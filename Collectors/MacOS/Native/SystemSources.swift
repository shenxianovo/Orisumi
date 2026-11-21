import AppKit
import ApplicationServices
import CoreGraphics

// Production adapters for the channel sources. Media sources live in MediaHardware.swift.

extension NSRunningApplication: RunningApplication {
    var key: AnyHashable { self }
    var pid: pid_t { processIdentifier }
    var bundleID: String? { bundleIdentifier }
    var name: String? { localizedName }
    var launchedAt: Date? { launchDate }
    var policy: ApplicationActivationPolicy? { ApplicationActivationPolicy(activationPolicy) }

    func watchPolicy(_ changed: @escaping (ApplicationActivationPolicy?) -> Void) -> Cancel {
        let observation = observe(\.activationPolicy, options: [.new]) { app, change in
            changed(app.isTerminated ? nil : ApplicationActivationPolicy(change.newValue ?? app.activationPolicy))
        }
        return { observation.invalidate() }
    }
}

private extension ApplicationActivationPolicy {
    init?(_ value: NSApplication.ActivationPolicy) {
        switch value {
        case .regular: self = .regular
        case .accessory: self = .accessory
        case .prohibited: self = .prohibited
        @unknown default: return nil
        }
    }
}

extension ApplicationsSource {
    static func workspace(_ workspace: NSWorkspace = .shared) -> ApplicationsSource {
        ApplicationsSource(
            running: { workspace.runningApplications },
            frontmost: { workspace.frontmostApplication },
            watchRunning: { changed in
                let observation = workspace.observe(\.runningApplications) { _, _ in changed() }
                return { observation.invalidate() }
            },
            watchActivation: { changed in
                let token = workspace.notificationCenter.addObserver(
                    forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
                ) { notification in
                    changed(notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)
                }
                return { workspace.notificationCenter.removeObserver(token) }
            })
    }
}

extension WindowSource {
    static let accessibility = WindowSource(permitted: { AXIsProcessTrusted() },
                                            open: { AccessibilityApplication(pid: $0, changed: $1) })
}

private final class AccessibilityApplication: WindowApplication {
    private let element: AXUIElement
    private let pid: pid_t
    private let changed: () -> Void
    private var observer: AXObserver?

    init(pid: pid_t, changed: @escaping () -> Void) {
        self.pid = pid
        self.changed = changed
        element = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(element, 0.25)
    }

    deinit {
        if let observer { CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes) }
    }

    func watchFocus() -> Bool { add(kAXFocusedWindowChangedNotification, to: element) }

    func watchTitle(of window: AnyObject) -> Bool { add(kAXTitleChangedNotification, to: window as! AXUIElement) }

    func unwatchTitle(of window: AnyObject) {
        if let observer { AXObserverRemoveNotification(observer, window as! AXUIElement, kAXTitleChangedNotification as CFString) }
    }

    func focusedWindow() -> WindowRead<AnyObject> {
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(element, kAXFocusedWindowAttribute as CFString, &value)
        if result == .success, let value, CFGetTypeID(value) == AXUIElementGetTypeID() { return .value(value) }
        return result == .noValue ? .none : .failed(result.rawValue)
    }

    func title(of window: AnyObject) -> WindowRead<String?> {
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(window as! AXUIElement, kAXTitleAttribute as CFString, &value)
        if result == .success { return .value(value as? String) }
        return result == .noValue || result == .attributeUnsupported ? .none : .failed(result.rawValue)
    }

    func isSameWindow(_ first: AnyObject, _ second: AnyObject) -> Bool { CFEqual(first, second) }

    private func add(_ name: String, to target: AXUIElement) -> Bool {
        if observer == nil {
            var created: AXObserver?
            guard AXObserverCreate(pid, { _, _, _, context in
                guard let context else { return }
                Unmanaged<AccessibilityApplication>.fromOpaque(context).takeUnretainedValue().changed()
            }, &created) == .success, let created else { return false }
            observer = created
            CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .commonModes)
        }
        let result = AXObserverAddNotification(observer!, target, name as CFString, Unmanaged.passUnretained(self).toOpaque())
        return result == .success || result == .notificationAlreadyRegistered
    }
}

extension InputSource {
    static let session = InputSource(
        permitted: { CGPreflightListenEventAccess() },
        idleSeconds: {
            CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: UInt32.max)!)
        },
        listen: { EventTap(receive: $0) })
}

private final class EventTap: InputTap {
    private static let activityTypes: Set<CGEventType> = [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged, .scrollWheel, .tabletPointer, .tabletProximity]
    private static let keyboardTypes: Set<CGEventType> = [.keyDown, .keyUp, .flagsChanged]
    private static let mouseKinds: [CGEventType: String] = [
        .leftMouseDown: "button_down", .rightMouseDown: "button_down", .otherMouseDown: "button_down",
        .leftMouseUp: "button_up", .rightMouseUp: "button_up", .otherMouseUp: "button_up",
    ]

    private let receive: (InputSignal) -> Void
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?

    init?(receive: @escaping (InputSignal) -> Void) {
        self.receive = receive
        let mask = Self.keyboardTypes.union(Self.mouseKinds.keys).union(Self.activityTypes)
            .reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly,
                                           eventsOfInterest: mask, callback: { _, type, event, context in
            if let context { Unmanaged<EventTap>.fromOpaque(context).takeUnretainedValue().handle(type, event) }
            return Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque()),
              let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        else { return nil }
        self.tap = tap
        self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        guard setEnabled(true) else {
            stop()
            return nil
        }
    }

    deinit { stop() }

    func setEnabled(_ enabled: Bool) -> Bool {
        guard let tap else { return false }
        CGEvent.tapEnable(tap: tap, enable: enabled)
        return CGEvent.tapIsEnabled(tap: tap) == enabled
    }

    func stop() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CFMachPortInvalidate(tap) }
        source = nil
        tap = nil
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            receive(.interrupted)
        } else if Self.keyboardTypes.contains(type) {
            receive(.key(type, keyCode: event.getIntegerValueField(.keyboardEventKeycode), flags: event.flags,
                         isRepeat: type == .keyDown && event.getIntegerValueField(.keyboardEventAutorepeat) != 0))
        } else if let kind = Self.mouseKinds[type] {
            let point = event.location
            receive(.mouse(kind: kind, xPoints: point.x, yPoints: point.y, button: event.getIntegerValueField(.mouseEventButtonNumber)))
        } else {
            receive(.activity)
        }
    }
}

enum SystemTime {
    static func timeZone() -> String { TimeZone.autoupdatingCurrent.identifier }

    // mach_continuous_time keeps counting during sleep.
    static func clock() -> (Date, Int64) {
        var timebase = mach_timebase_info_data_t()
        mach_timebase_info(&timebase)
        let ticks = mach_continuous_time()
        return (Date(), Int64(ticks * UInt64(timebase.numer) / UInt64(timebase.denom)))
    }

    static func notification(_ name: Notification.Name) -> SystemNotification {
        { changed in
            let token = NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in changed() }
            return { NotificationCenter.default.removeObserver(token) }
        }
    }
}
