import Foundation
import CoreGraphics

// Every channel follows the CaptureChannel contract against stand-in sources: start records an initial value,
// an unchanged reconcile writes nothing, interrupt records the channel unavailable, resume records a new initial value.

func check(_ value: Bool, _ message: String) {
    if !value { fatalError(message) }
}

var observations: [[String: Any]] = []
let events = ObservationEvents { data in
    let batch = try JSONSerialization.jsonObject(with: data) as! [String: Any]
    observations.append(contentsOf: batch["events"] as! [[String: Any]])
    FileHandle.standardOutput.write(data)
}
func matching(_ type: String, where predicate: ([String: Any]) -> Bool = { _ in true }) -> [[String: Any]] {
    try! events.flush()
    return observations.filter { $0["type"] as? String == type && predicate($0) }
}
func rows(_ type: String) -> Int { matching(type).count }
func allRows() -> Int {
    try! events.flush()
    return observations.count
}

final class FakeApplication: RunningApplication {
    let pid: pid_t
    let bundleID: String?
    let name: String?
    let launchedAt: Date?
    var isTerminated = false
    var policy: ApplicationActivationPolicy? = .regular
    var key: AnyHashable { ObjectIdentifier(self) }

    init(pid: pid_t, bundleID: String, launch: TimeInterval = 1000) {
        self.pid = pid
        self.bundleID = bundleID
        name = bundleID
        launchedAt = Date(timeIntervalSince1970: launch)
    }

    private var changed: ((ApplicationActivationPolicy?) -> Void)?
    func watchPolicy(_ changed: @escaping (ApplicationActivationPolicy?) -> Void) -> Cancel {
        self.changed = changed
        return { self.changed = nil }
    }
    func changePolicy(to policy: ApplicationActivationPolicy) {
        self.policy = policy
        changed?(policy)
    }
}

var running: [FakeApplication] = [FakeApplication(pid: 900001, bundleID: "fixture.editor"), FakeApplication(pid: 900002, bundleID: "fixture.browser")]
var frontmost: FakeApplication? = running[0]
var activated: ((RunningApplication?) -> Void)?
var runningChanged: (() -> Void)?
let applicationsSource = ApplicationsSource(
    running: { running }, frontmost: { frontmost },
    watchRunning: { changed in runningChanged = changed; return { runningChanged = nil } },
    watchActivation: { changed in activated = changed; return { activated = nil } })

final class FakeWindowApplication: WindowApplication {
    final class Window {}
    var window: Window? = Window()
    var title: String? = "Draft"
    var focusError: Int32?
    let changed: () -> Void

    init(changed: @escaping () -> Void) { self.changed = changed }
    func watchFocus() -> Bool { true }
    func watchTitle(of window: AnyObject) -> Bool { true }
    func unwatchTitle(of window: AnyObject) {}
    func focusedWindow() -> WindowRead<AnyObject> {
        if let focusError { return .failed(focusError) }
        return window.map { .value($0) } ?? .none
    }
    func title(of window: AnyObject) -> WindowRead<String?> { .value(title) }
    func isSameWindow(_ first: AnyObject, _ second: AnyObject) -> Bool { first === second }
}

var windows: [pid_t: FakeWindowApplication] = [:]
let windowSource = WindowSource(permitted: { true }, open: { pid, changed in
    let application = FakeWindowApplication(changed: changed)
    application.focusError = pid == 900002 ? -25205 : nil
    windows[pid] = application
    return application
})

final class FakeTap: InputTap {
    var enabled = true
    func setEnabled(_ enabled: Bool) -> Bool { self.enabled = enabled; return true }
    func stop() { enabled = false }
}
var receive: ((InputSignal) -> Void)?
let inputSource = InputSource(permitted: { true }, idleSeconds: { 1 }, listen: { callback in
    receive = callback
    return FakeTap()
})

var cameras: [UInt32] = [7]
let cameraSource = MediaObjectSource(
    list: { cameras }, watchList: { _ in {} }, watchRestart: nil,
    identity: { _ in .camera(uid: "fixture-camera", name: "Camera") }, matchApplication: nil,
    read: { _, _ in .success(false) }, watch: { _, _, _ in {} })

var zone = "Asia/Shanghai"
var monotonic: Int64 = 1_000_000

let failed: (Error) -> Void = { fatalError("\($0)") }
let applications = ApplicationsChannel(source: applicationsSource, events: events, failed: failed)
let channels: [(CaptureChannel, facts: String, names: [String])] = [
    (applications, "workspace", ["applications"]),
    (WindowsChannel(source: windowSource, applications: applications, events: events, failed: failed), "window", ["windows", "window_title"]),
    (InputChannel(source: inputSource, captureInput: true, afkThresholdMS: 300_000, events: events, failed: failed), "afk", ["afk", "input"]),
    (MediaObjectObserver(source: cameraSource, channels: [.camera], kind: .cameraDevice, events: events, failed: failed), "media_presence", ["camera"]),
    (TimeZoneChannel(read: { zone }, watch: { _ in {} }, events: events, failed: failed), "time_zone", []),
    (ClockChannel(read: { monotonic += 1; return (Date(), monotonic) }, watch: { _ in {} }, events: events, failed: failed), "clock", []),
]

for (channel, facts, names) in channels {
    let before = rows(facts)
    try channel.start()
    check(rows(facts) > before, "\(facts): start records an initial value")
    let started = allRows()
    try channel.reconcile()
    check(allRows() == started, "\(facts): an unchanged reconcile writes nothing")
    try channel.interrupt(reason: "session_paused")
    for name in names {
        let latest = matching("channel") { $0["channel"] as? String == name }.last
        check(latest?["available"] as? Bool == false && latest?["reason"] as? String == "session_paused",
              "\(name): interrupt records the channel unavailable")
    }
    let interrupted = rows(facts)
    try channel.resume()
    check(rows(facts) > interrupted, "\(facts): resume records a new initial value")
    let resumed = allRows()
    try channel.reconcile()
    check(allRows() == resumed, "\(facts): an unchanged reconcile after resume writes nothing")
}

// Foreground changes reach the windows channel through the applications channel.
let previousFocusRows = rows("window")
frontmost = running[1]
activated?(running[1])
check(matching("workspace") { $0["kind"] as? String == "activated" }.count == 1, "activation is recorded")
let failedFocus = matching("window").dropFirst(previousFocusRows)
check(failedFocus.count == 1 && failedFocus.first?["kind"] as? String == "read_failed"
      && failedFocus.first?["hasWindow"] is NSNull, "a failed switch makes the previous app's focus unknown")
let browserWindow = windows[900002]!
browserWindow.changed()
check(rows("window") == previousFocusRows + 1, "repeated failure leaves focus unknown")
check(matching("read_failure") { $0["error"] as? String == "windows: read_failed ax=-25205 pid=900002" }.count == 1,
      "the focus read failure identifies the AX error and target PID")
browserWindow.focusError = nil
browserWindow.changed()
let recoveredFocus = matching("window").last
check(recoveredFocus?["kind"] as? String == "snapshot" && recoveredFocus?["hasWindow"] as? Bool == true,
      "focus recovery starts a new snapshot")
windows[900002]!.title = "Inbox"
windows[900002]!.changed()
check(matching("window") { $0["title"] as? String == "Inbox" }.count == 1, "a title notification records the change")
browserWindow.window = nil
browserWindow.changed()
check(matching("window").last?["hasWindow"] as? Bool == false, "no focused window is a known state")

// An accessory is admitted on its first regular policy or foreground observation, then tracked
// through policy demotion and a verified session gap. Its successor starts with a clean policy.
let promoted = FakeApplication(pid: 900003, bundleID: "fixture.promoted")
promoted.policy = .accessory
let helper = FakeApplication(pid: 900004, bundleID: "fixture.helper")
helper.policy = .prohibited
running.append(contentsOf: [promoted, helper])
runningChanged?()
check(applications.observedInstance(pid: promoted.pid, bundleID: nil) == nil, "accessory has no instance yet")
check(applications.observedInstance(pid: helper.pid, bundleID: nil) == nil, "helper never qualifies")
promoted.changePolicy(to: .regular)
let admitted = applications.observedInstance(pid: promoted.pid, bundleID: nil)
check(admitted != nil, "KVO promotion qualifies the application")
promoted.changePolicy(to: .accessory)
try applications.interrupt(reason: "session_paused")
try applications.resume()
check(applications.observedInstance(pid: promoted.pid, bundleID: nil) == admitted, "verified accessory keeps prior eligibility")
running.removeAll { $0 === promoted }
runningChanged?()
let successor = FakeApplication(pid: promoted.pid, bundleID: "fixture.successor", launch: 2000)
successor.policy = .accessory
running.append(successor)
runningChanged?()
check(applications.observedInstance(pid: successor.pid, bundleID: nil) == nil, "PID reuse does not convey eligibility")

let sampled = FakeApplication(pid: 900006, bundleID: "fixture.sampled-policy")
sampled.policy = .prohibited
running.append(sampled)
runningChanged?()
sampled.policy = .regular
try applications.reconcile()
check(applications.observedInstance(pid: sampled.pid, bundleID: nil) != nil, "reconciliation catches a missed policy notification")

let foregroundAccessory = FakeApplication(pid: 900005, bundleID: "fixture.foreground-accessory")
foregroundAccessory.policy = .accessory
frontmost = foregroundAccessory
activated?(foregroundAccessory)
check(applications.observedInstance(pid: foregroundAccessory.pid, bundleID: nil) != nil, "foreground qualifies before running-list notification")
windows[900005]!.title = "Loading…"
windows[900005]!.changed()
check(matching("window") { $0["title"] as? String == "Loading…" }.count == 1, "loading titles are kept verbatim")
check(matching("process") { ["fixture.helper", "fixture.successor"].contains($0["bundle"] as? String ?? "") }.isEmpty,
      "unqualified processes never appear in the stream")

let lockKey = "CGSSessionScreenIsLocked"
check(ScreenStateSource.lockState(nil) == nil, "unreadable session gives an unknown lock state")
check(ScreenStateSource.lockState([:]) == nil, "an absent private key is unknown without a verified fallback")
check(ScreenStateSource.lockState([lockKey: true]) == true, "a true private key gives a locked state")
check(ScreenStateSource.lockState([lockKey: false]) == false, "a false private key gives an unlocked state")
var lockSession: [String: Any]? = [:]
var lockChanged: ((Bool) -> Void)?
var displayChanged: ((Bool) -> Void)?
var displayInitial: Bool? = false
let locked = ScreenStateObserver(.lock, source: ScreenStateSource(read: {
    check(lockChanged != nil, "screen lock is watched before reading its initial value")
    return ScreenStateSource.lockState(lockSession)
}, watch: { callback in lockChanged = callback; return { lockChanged = nil } }), events: events, failed: failed)
let display = ScreenStateObserver(.displaySleep, source: ScreenStateSource(read: {
    check(displayChanged != nil, "display sleep is watched before reading its initial value")
    return displayInitial
}, watch: { callback in displayChanged = callback; return { displayChanged = nil } }), events: events, failed: failed)
let channelCount = rows("channel")
try locked.start()
try display.start()
lockChanged?(true)
lockChanged?(true)
displayChanged?(true)
lockChanged?(false)
displayChanged?(false)
check(rows("channel") == channelCount, "screen observations do not interrupt other channels")
displayInitial = nil
lockSession = [lockKey: true]
try locked.snapshot()
try display.snapshot()
locked.stop()
display.stop()
check(lockChanged == nil && displayChanged == nil, "screen notification observers are removed")

// Input: a press is buffered until flushed; an interrupted tap is recorded and re-enabled.
receive?(.key(.keyDown, keyCode: 12, flags: [], isRepeat: false))
receive?(.key(.keyUp, keyCode: 12, flags: [], isRepeat: false))
receive?(.interrupted)
check(matching("channel") {
    $0["channel"] as? String == "input" && ["tap_interrupted", "tap_reenabled"].contains($0["reason"] as? String ?? "")
}.count == 2, "an interrupted tap is recorded and re-enabled")

for (channel, _, _) in channels.reversed() { try channel.stop() }
check(rows("keyboard") == 1, "stop writes buffered input")
try events.flush(stopped: true)
fputs("PASS: capture channel contract and cross-channel changes\n", stderr)
