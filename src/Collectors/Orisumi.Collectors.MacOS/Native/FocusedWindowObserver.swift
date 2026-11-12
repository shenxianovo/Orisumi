import AppKit
import ApplicationServices
import CoreFoundation

final class FocusedWindowObserver {
    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var pid: pid_t?
    private var application: AXUIElement?
    private var observer: AXObserver?
    private var window: AXUIElement?
    private var title: String?
    private var hasSample = false
    private var pendingKind: WindowEventKind = .snapshot

    init(database: ForegroundDatabase, failed: @escaping (Error) -> Void) {
        self.database = database
        self.failed = failed
    }

    deinit { detach() }

    func watch(_ app: NSRunningApplication?, kind: WindowEventKind) throws {
        if kind == .focusChanged, let pid, app?.processIdentifier == pid {
            attachObserver()
            try sample()
            return
        }
        detach()
        guard let pid = app?.processIdentifier, pid > 0 else { return }
        self.pid = pid
        pendingKind = kind
        let application = AXUIElementCreateApplication(pid)
        self.application = application
        AXUIElementSetMessagingTimeout(application, 0.25)
        attachObserver()
        try sample()
    }

    private func attachObserver() {
        guard observer == nil, let application, let pid else { return }
        var created: AXObserver?
        let result = AXObserverCreate(pid, { observer, _, _, context in
            guard let context else { return }
            let target = Unmanaged<FocusedWindowObserver>.fromOpaque(context).takeUnretainedValue()
            guard let current = target.observer, CFEqual(observer, current),
                  NSWorkspace.shared.frontmostApplication?.processIdentifier == target.pid
            else { return }
            do {
                try target.sample()
            } catch {
                target.failed(error)
            }
        }, &created)
        if result == .success, let created {
            observer = created
            CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .commonModes)
            addNotification(kAXFocusedWindowChangedNotification, to: application)
            if let window { addNotification(kAXTitleChangedNotification, to: window) }
        }
    }

    func detach() {
        if let observer {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
        }
        observer = nil
        application = nil
        pid = nil
        window = nil
        title = nil
        hasSample = false
    }

    private func addNotification(_ name: String, to element: AXUIElement) {
        guard let observer else { return }
        AXObserverAddNotification(observer, element, name as CFString, Unmanaged.passUnretained(self).toOpaque())
    }

    private func sample() throws {
        guard let pid, let application else { return }
        let observedAt = Date()
        var focusedValue: CFTypeRef?
        let focusResult = AXUIElementCopyAttributeValue(application, kAXFocusedWindowAttribute as CFString, &focusedValue)
        let focusedWindow: AXUIElement?
        if focusResult == .success, let focusedValue, CFGetTypeID(focusedValue) == AXUIElementGetTypeID() {
            focusedWindow = (focusedValue as! AXUIElement)
        } else if focusResult == .noValue {
            focusedWindow = nil
        } else {
            return
        }

        let windowChanged: Bool
        if let window, let focusedWindow {
            windowChanged = !CFEqual(window, focusedWindow)
        } else {
            windowChanged = (window != nil) != (focusedWindow != nil)
        }
        if windowChanged {
            if let observer, let window {
                AXObserverRemoveNotification(observer, window, kAXTitleChangedNotification as CFString)
            }
            if let focusedWindow {
                addNotification(kAXTitleChangedNotification, to: focusedWindow)
            }
        }

        var focusedTitle: String?
        if let focusedWindow {
            var titleValue: CFTypeRef?
            let titleResult = AXUIElementCopyAttributeValue(focusedWindow, kAXTitleAttribute as CFString, &titleValue)
            if titleResult == .success {
                focusedTitle = titleValue as? String
            } else if !windowChanged && hasSample && titleResult != .noValue && titleResult != .attributeUnsupported {
                focusedTitle = title
            }
        }
        if !hasSample {
            try database.appendWindow(pendingKind, at: observedAt, pid: pid, title: focusedTitle)
        } else if windowChanged {
            try database.appendWindow(.focusChanged, at: observedAt, pid: pid, title: focusedTitle)
        } else if focusedTitle != title {
            try database.appendWindow(.titleChanged, at: observedAt, pid: pid, title: focusedTitle)
        }
        window = focusedWindow
        title = focusedTitle
        hasSample = true
    }
}
