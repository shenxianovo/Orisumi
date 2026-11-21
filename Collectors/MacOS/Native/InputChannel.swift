import CoreGraphics
import Foundation

enum InputEvent {
    case keyboardPress(KeyboardPress)
    case mouse(kind: String, xPoints: Double, yPoints: Double, button: Int64)
}

struct InputObservation {
    let token: Int64
    let observedAt: Date
    let event: InputEvent
}

enum InputCaptureError: LocalizedError {
    case tapUnavailable
    case bufferFull

    var errorDescription: String? {
        switch self {
        case .tapUnavailable: "could not listen to keyboard and mouse events in this macOS session"
        case .bufferFull: "the input event buffer filled before it could be written"
        }
    }
}

enum InputSignal {
    case key(CGEventType, keyCode: Int64, flags: CGEventFlags, isRepeat: Bool)
    case mouse(kind: String, xPoints: Double, yPoints: Double, button: Int64)
    /// Movement, scrolling and other input that only proves the person is present.
    case activity
    /// The system disabled the listener; events since then are missing.
    case interrupted
}

// A keyboard and mouse listener; a CGEvent tap in production.
protocol InputTap: AnyObject {
    /// Returns whether the listener is enabled afterwards.
    func setEnabled(_ enabled: Bool) -> Bool
    func stop()
}

struct InputSource {
    let permitted: () -> Bool
    /// Seconds since the last input in this session, or nil when it cannot be read.
    let idleSeconds: () -> Double?
    let listen: (@escaping (InputSignal) -> Void) -> InputTap?
}

// Keyboard presses and mouse buttons when enabled, and whether the person is active or idle (AFK).
// AFK follows the listener when it runs and samples the system idle time otherwise.
final class InputChannel: CaptureChannel {
    private let source: InputSource
    private let events: ObservationEvents
    private let failed: (Error) -> Void
    private let captureInput: Bool
    private var permission = CapturePermissionState()
    private var idle: ActivityIdleTracker
    private var idleTimer: CFRunLoopTimer?
    private var tap: InputTap?
    private var flushTimer: Timer?
    private var pending: [InputObservation] = []
    private var keyboard = KeyboardPressAccumulator()
    private var listening = false
    private var reportedTapFailure = false

    init(source: InputSource, captureInput: Bool, afkThresholdMS: Int64, events: ObservationEvents, failed: @escaping (Error) -> Void) {
        self.source = source
        self.captureInput = captureInput
        self.events = events
        self.failed = failed
        idle = ActivityIdleTracker(threshold: TimeInterval(afkThresholdMS) / 1000)
    }

    func start() throws {
        listening = true
        try sampleAFK()
        try refreshListener()
    }

    func interrupt(reason: String) throws {
        listening = false
        resetIdle()
        try events.appendChannel("afk", available: false, reason: reason)
        if tap != nil {
            pending.append(contentsOf: keyboard.interrupt())
            _ = tap?.setEnabled(false)
            try events.appendChannel("input", available: false, reason: reason)
        }
    }

    func resume() throws {
        listening = true
        if let tap {
            if tap.setEnabled(true) {
                try events.appendChannel("input", available: true, reason: "listener_resumed")
            } else {
                try stopListener(reason: "tap_unavailable")
            }
        }
        try sampleAFK()
        try refreshListener()
    }

    func reconcile() throws {
        try refreshListener()
        if tap == nil { try sampleAFK() }
    }

    func stop() throws {
        listening = false
        pending.append(contentsOf: keyboard.interrupt())
        closeTap()
        try flush()
    }

    func flush() throws {
        guard !pending.isEmpty else { return }
        try events.appendInputBatch(pending)
        pending.removeAll(keepingCapacity: true)
    }

    private func refreshListener() throws {
        guard captureInput else { return }
        let granted = source.permitted()
        try permission.observe(granted) { event in
            try events.appendPermission(.inputMonitoring, kind: event, at: Date())
        }
        guard granted else {
            try stopListener(reason: "listener_stopped")
            return
        }
        guard listening, tap == nil else { return }
        guard let tap = source.listen({ [weak self] signal in self?.receive(signal) }) else {
            try events.appendChannel("input", available: false, reason: "tap_unavailable")
            if !reportedTapFailure {
                fputs("orisumi: input listener unavailable; will retry\n", stderr)
                reportedTapFailure = true
            }
            return
        }
        self.tap = tap
        let timer = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in
            guard let self else { return }
            do { try self.flush() } catch { self.closeTap(); self.failed(error) }
        }
        flushTimer = timer
        RunLoop.main.add(timer, forMode: .common)
        try events.appendChannel("input", available: true, reason: "listener_started")
        reportedTapFailure = false
        try sampleAFK()
    }

    private func stopListener(reason: String) throws {
        let wasListening = tap != nil
        pending.append(contentsOf: keyboard.interrupt())
        closeTap()
        try flush()
        if wasListening { resetIdle() }
        try events.appendChannel("input", available: false, reason: reason)
    }

    private func closeTap() {
        flushTimer?.invalidate()
        flushTimer = nil
        tap?.stop()
        tap = nil
    }

    private func receive(_ signal: InputSignal) {
        guard listening, let tap else { return }
        let observedAt = Date()
        switch signal {
        case .interrupted:
            fputs("orisumi: the input event tap was interrupted; some input events may be missing\n", stderr)
            do {
                try events.appendChannel("input", available: false, reason: "tap_interrupted")
                pending.append(contentsOf: keyboard.interrupt())
                resetIdle()
                try sampleAFK()
                if tap.setEnabled(true) {
                    try events.appendChannel("input", available: true, reason: "tap_reenabled")
                } else {
                    try stopListener(reason: "tap_unavailable")
                }
            } catch {
                closeTap()
                failed(error)
            }
            return
        case let .key(type, keyCode, flags, isRepeat):
            activity()
            do {
                if let press = try keyboard.consume(type, keyCode: keyCode, flags: flags, isRepeat: isRepeat, at: observedAt,
                                                    reserve: { try events.reserveInput(at: observedAt) }) {
                    enqueue(press)
                }
            } catch { failed(error) }
        case let .mouse(kind, x, y, button):
            activity()
            do {
                enqueue(InputObservation(token: try events.reserveInput(at: observedAt), observedAt: observedAt,
                                         event: .mouse(kind: kind, xPoints: x, yPoints: y, button: button)))
            } catch { failed(error) }
        case .activity:
            activity()
        }
    }

    private func enqueue(_ observation: InputObservation) {
        guard pending.count < 8192 else {
            closeTap()
            failed(InputCaptureError.bufferFull)
            return
        }
        pending.append(observation)
    }

    private func activity() {
        do {
            try recordIdle(idle.activity(at: ProcessInfo.processInfo.systemUptime))
            scheduleIdle()
        } catch { failed(error) }
    }

    private func sampleAFK() throws {
        guard listening else { return }
        guard let seconds = source.idleSeconds(), seconds.isFinite, seconds >= 0, seconds < Double(Int64.max) / 1000 else {
            resetIdle()
            try events.appendChannel("afk", available: false, reason: "read_failed")
            return
        }
        try events.appendChannel("afk", available: true, reason: "sample_received")
        try events.appendMethod("afk", mode: tap == nil ? "snapshot" : "notification")
        try recordIdle(idle.observe(idle: seconds, at: ProcessInfo.processInfo.systemUptime))
        scheduleIdle()
    }

    private func recordIdle(_ change: (AFKEventKind, AFKState, Int64)?) throws {
        if let (kind, state, idleMS) = change {
            try events.appendAFK(kind, at: Date(), state: state, idleMS: idleMS)
        }
    }

    private func resetIdle() {
        if let idleTimer { CFRunLoopTimerInvalidate(idleTimer) }
        idleTimer = nil
        idle.reset()
    }

    // With a listener, idle is a deadline after the last input rather than a sample.
    private func scheduleIdle() {
        guard idleTimer == nil, tap != nil, listening,
              let remaining = idle.remaining(at: ProcessInfo.processInfo.systemUptime) else { return }
        let timer = CFRunLoopTimerCreateWithHandler(kCFAllocatorDefault,
            CFAbsoluteTimeGetCurrent() + max(0.001, remaining), 0, 0, 0) { [weak self] _ in
            guard let self else { return }
            self.idleTimer = nil
            guard self.listening else { return }
            do {
                try self.recordIdle(self.idle.tick(at: ProcessInfo.processInfo.systemUptime))
                self.scheduleIdle()
            } catch { self.failed(error) }
        }
        idleTimer = timer
        CFRunLoopAddTimer(CFRunLoopGetMain(), timer, .commonModes)
    }
}
