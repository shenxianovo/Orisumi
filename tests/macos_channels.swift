import Foundation
import CoreGraphics
import SQLite3

// Every channel follows the CaptureChannel contract against stand-in sources: start records an initial value,
// an unchanged reconcile writes nothing, interrupt records the channel unavailable, resume records a new initial value.

func check(_ value: Bool, _ message: String) {
    if !value { fatalError(message) }
}

let path = CommandLine.arguments[1]
func query(_ sql: String) -> Int64 {
    var connection: OpaquePointer?
    var statement: OpaquePointer?
    defer { sqlite3_finalize(statement); sqlite3_close(connection) }
    guard sqlite3_open_v2(path, &connection, SQLITE_OPEN_READONLY, nil) == SQLITE_OK,
          sqlite3_prepare_v2(connection, sql, -1, &statement, nil) == SQLITE_OK,
          sqlite3_step(statement) == SQLITE_ROW else { fatalError("query failed: \(sql)") }
    return sqlite3_column_int64(statement, 0)
}
func rows(_ table: String) -> Int64 { query("SELECT COUNT(*) FROM \(table)") }
func allRows() -> Int64 {
    var connection: OpaquePointer?
    var statement: OpaquePointer?
    defer { sqlite3_finalize(statement); sqlite3_close(connection) }
    guard sqlite3_open_v2(path, &connection, SQLITE_OPEN_READONLY, nil) == SQLITE_OK,
          sqlite3_prepare_v2(connection, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*'", -1, &statement, nil) == SQLITE_OK
    else { fatalError("could not list tables") }
    var tables: [String] = []
    while sqlite3_step(statement) == SQLITE_ROW { tables.append(String(cString: sqlite3_column_text(statement, 0))) }
    return tables.reduce(0) { $0 + rows($1) }
}

final class FakeApplication: RunningApplication {
    let pid: pid_t
    let bundleID: String?
    let name: String?
    let launchedAt: Date? = Date(timeIntervalSince1970: 1000)
    var isTerminated = false
    var policy: ApplicationActivationPolicy? = .regular
    var key: AnyHashable { ObjectIdentifier(self) }

    init(pid: pid_t, bundleID: String) {
        self.pid = pid
        self.bundleID = bundleID
        name = bundleID
    }

    func watchPolicy(_ changed: @escaping (ApplicationActivationPolicy?) -> Void) -> Cancel { {} }
}

var running: [FakeApplication] = [FakeApplication(pid: 900001, bundleID: "fixture.editor"), FakeApplication(pid: 900002, bundleID: "fixture.browser")]
var frontmost: FakeApplication? = running[0]
var activated: ((RunningApplication?) -> Void)?
let applicationsSource = ApplicationsSource(
    running: { running }, frontmost: { frontmost },
    watchRunning: { _ in {} },
    watchActivation: { changed in activated = changed; return { activated = nil } })

final class FakeWindowApplication: WindowApplication {
    final class Window {}
    var window: Window? = Window()
    var title: String? = "Draft"
    let changed: () -> Void

    init(changed: @escaping () -> Void) { self.changed = changed }
    func watchFocus() -> Bool { true }
    func watchTitle(of window: AnyObject) -> Bool { true }
    func unwatchTitle(of window: AnyObject) {}
    func focusedWindow() -> WindowRead<AnyObject> { window.map { .value($0) } ?? .none }
    func title(of window: AnyObject) -> WindowRead<String?> { .value(title) }
    func isSameWindow(_ first: AnyObject, _ second: AnyObject) -> Bool { first === second }
}

var windows: [pid_t: FakeWindowApplication] = [:]
let windowSource = WindowSource(permitted: { true }, open: { pid, changed in
    let application = FakeWindowApplication(changed: changed)
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
    read: { _, _ in false }, watch: { _, _, _ in {} })

var zone = "Asia/Shanghai"
var monotonic: Int64 = 1_000_000

// The host the .NET platform layer would pass; the helper records it as given.
let host = HostObservation(platform: "macos", hardwareUuid: "00000000-0000-0000-0000-000000000001", hostName: "fixture",
                           model: nil, osVersion: nil, osBuild: nil, bootId: "fixture-boot", userId: 501, userName: "fixture",
                           storePath: "/fixture/store")
let database = try ForegroundDatabase(path: path, host: host, captureWindows: true, captureInput: true, mediaChannels: [.camera])
try database.start(at: Date(), afkThresholdMS: 300_000)
let failed: (Error) -> Void = { fatalError("\($0)") }
let applications = ApplicationsChannel(source: applicationsSource, database: database, failed: failed)
let channels: [(CaptureChannel, facts: String, names: [String])] = [
    (applications, "foreground_app_events", ["applications"]),
    (WindowsChannel(source: windowSource, applications: applications, database: database, failed: failed), "focused_window_events", ["windows", "window_title"]),
    (InputChannel(source: inputSource, captureInput: true, afkThresholdMS: 300_000, database: database, failed: failed), "afk_events", ["afk", "input"]),
    (MediaObjectObserver(source: cameraSource, channels: [.camera], kind: .cameraDevice, database: database, failed: failed), "camera_device_presence_events", ["camera"]),
    (TimeZoneChannel(read: { zone }, watch: { _ in {} }, database: database, failed: failed), "time_zone_events", []),
    (ClockChannel(read: { monotonic += 1; return (Date(), monotonic) }, watch: { _ in {} }, database: database, failed: failed), "clock_events", []),
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
        check(query("SELECT available=0 AND reason='session_paused' FROM capture_channel_events WHERE channel='\(name)' ORDER BY run_sequence DESC LIMIT 1") == 1,
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
frontmost = running[1]
activated?(running[1])
check(query("SELECT COUNT(*) FROM foreground_app_events WHERE event_kind='activated'") == 1, "activation is recorded")
check(query("SELECT COUNT(*) FROM focused_window_events WHERE event_kind='focus_changed'") == 1, "activation moves the focused window")
windows[900002]!.title = "Inbox"
windows[900002]!.changed()
check(query("SELECT COUNT(*) FROM window_title_observations WHERE title='Inbox'") == 1, "a title notification records the change")

// Input: a press is buffered until flushed; an interrupted tap is recorded and re-enabled.
receive?(.key(.keyDown, keyCode: 12, flags: [], isRepeat: false))
receive?(.key(.keyUp, keyCode: 12, flags: [], isRepeat: false))
receive?(.interrupted)
check(query("SELECT COUNT(*) FROM capture_channel_events WHERE channel='input' AND reason IN ('tap_interrupted','tap_reenabled')") == 2,
      "an interrupted tap is recorded and re-enabled")

for (channel, _, _) in channels.reversed() { try channel.stop() }
check(rows("keyboard_presses") == 1, "stop writes buffered input")
try database.stop(at: Date())
print("PASS: capture channel contract and cross-channel changes")
