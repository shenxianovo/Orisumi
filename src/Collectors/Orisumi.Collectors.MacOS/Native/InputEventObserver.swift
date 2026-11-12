import CoreFoundation
import CoreGraphics
import Foundation

enum InputEvent {
    case keyboardPress(KeyboardPress)
    case mouse(kind: String, xPoints: Double, yPoints: Double, button: Int64)
}

struct InputObservation {
    let runSequence: Int64
    let observedAt: Date
    let event: InputEvent
}

private enum InputCaptureError: LocalizedError {
    case tapUnavailable
    case bufferFull

    var errorDescription: String? {
        switch self {
        case .tapUnavailable: "could not listen to keyboard and mouse events in this macOS session"
        case .bufferFull: "the input event buffer filled before it could be written"
        }
    }
}

final class InputEventObserver {
    private static let keyboardTypes: Set<CGEventType> = [.keyDown, .keyUp, .flagsChanged]
    private static let mouseKinds: [CGEventType: String] = [
        .leftMouseDown: "button_down", .rightMouseDown: "button_down", .otherMouseDown: "button_down",
        .leftMouseUp: "button_up", .rightMouseUp: "button_up", .otherMouseUp: "button_up",
    ]

    private let database: ForegroundDatabase
    private let failed: (Error) -> Void
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var timer: Timer?
    private var pending: [InputObservation] = []
    private var keyboard = KeyboardPressAccumulator()
    private var paused = false

    init(database: ForegroundDatabase, failed: @escaping (Error) -> Void) throws {
        self.database = database
        self.failed = failed

        let eventMask = Self.keyboardTypes.union(Self.mouseKinds.keys)
            .reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly,
                                           eventsOfInterest: eventMask, callback: { _, type, event, context in
            if let context {
                Unmanaged<InputEventObserver>.fromOpaque(context).takeUnretainedValue().receive(type, event: event)
            }
            return Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque()),
              let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        else { throw InputCaptureError.tapUnavailable }

        self.tap = tap
        self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        guard CGEvent.tapIsEnabled(tap: tap) else {
            stop()
            throw InputCaptureError.tapUnavailable
        }

        let timer = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in
            guard let self else { return }
            do {
                try self.flush()
            } catch {
                self.stop()
                self.failed(error)
            }
        }
        self.timer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    deinit { stop() }

    func setPaused(_ paused: Bool) throws {
        guard let tap, paused != self.paused else { return }
        self.paused = paused
        if paused { pending.append(contentsOf: keyboard.interrupt()) }
        CGEvent.tapEnable(tap: tap, enable: !paused)
        if !paused && !CGEvent.tapIsEnabled(tap: tap) {
            throw InputCaptureError.tapUnavailable
        }
    }

    func stop() {
        pending.append(contentsOf: keyboard.interrupt())
        timer?.invalidate()
        timer = nil
        if let tap {
            CGEvent.tapEnable(tap: tap, enable: false)
        }
        if let source {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let tap {
            CFMachPortInvalidate(tap)
        }
        source = nil
        tap = nil
    }

    func flush() throws {
        guard !pending.isEmpty else { return }
        try database.appendInputBatch(pending)
        pending.removeAll(keepingCapacity: true)
    }

    private func receive(_ type: CGEventType, event: CGEvent) {
        guard let tap, !paused else { return }
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            fputs("orisumi: the input event tap was interrupted; some input events may be missing\n", stderr)
            pending.append(contentsOf: keyboard.interrupt())
            CGEvent.tapEnable(tap: tap, enable: true)
            if !CGEvent.tapIsEnabled(tap: tap) {
                stop()
                failed(InputCaptureError.tapUnavailable)
            }
            return
        }

        let observedAt = Date()
        if Self.keyboardTypes.contains(type) {
            let isRepeat = type == .keyDown && event.getIntegerValueField(.keyboardEventAutorepeat) != 0
            if let press = keyboard.consume(type, keyCode: event.getIntegerValueField(.keyboardEventKeycode),
                                            flags: event.flags, isRepeat: isRepeat, at: observedAt,
                                            reserveSequence: database.reserveInputSequence) {
                enqueue(press)
            }
            return
        }

        guard let kind = Self.mouseKinds[type] else { return }
        let point = event.location
        let observation = InputEvent.mouse(kind: kind, xPoints: point.x, yPoints: point.y,
                                           button: event.getIntegerValueField(.mouseEventButtonNumber))
        enqueue(InputObservation(runSequence: database.reserveInputSequence(), observedAt: observedAt, event: observation))
    }

    private func enqueue(_ observation: InputObservation) {
        guard pending.count < 8192 else {
            stop()
            failed(InputCaptureError.bufferFull)
            return
        }
        pending.append(observation)
    }
}
