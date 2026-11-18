import Foundation
import CoreGraphics

func check(_ value: Bool, _ message: String) {
    if !value { fatalError(message) }
}

var idle = ActivityIdleTracker(threshold: 10)
check(idle.observe(idle: 0, at: 100)?.0 == .snapshot, "initial activity baseline")
check(idle.activity(at: 109) == nil, "movement does not repeat active state")
check(idle.tick(at: 110) == nil && idle.remaining(at: 110) == 9, "an old deadline follows the latest activity")
check(idle.tick(at: 119)?.1 == .idle, "deadline emits idle")
check(idle.activity(at: 120)?.1 == .active, "activity exits idle immediately")
idle.reset()
check(idle.observe(idle: 20, at: 200)?.0 == .snapshot, "recovery starts a new baseline")
check(idle.observe(idle: 21, at: 201) == nil, "fallback sampling stays sparse")

var schedule = WindowObservationSchedule()
check(schedule.shouldRead(at: 0), "no baseline needs a read")
schedule = WindowObservationSchedule(focusNotification: true, titleNotification: true,
    hasWindow: true, readSucceeded: true, lastRead: 0)
check(!schedule.shouldRead(at: 1), "supported notifications avoid per-second reads")
check(schedule.shouldRead(at: 60), "supported notifications still reconcile")
schedule.titleNotification = false
check(schedule.shouldRead(at: 1), "unsupported title notification requires sampling")
schedule.hasWindow = false
check(!schedule.shouldRead(at: 1), "no focused window needs no title notification")
schedule.readSucceeded = false
check(schedule.shouldRead(at: 1), "read failure retries")

var keyboard = KeyboardPressAccumulator()
var sequence: Int64 = 0
func reserve() -> Int64 { sequence += 1; return sequence }
_ = keyboard.consume(.keyDown, keyCode: 42, flags: [], isRepeat: false, at: Date(), reserveSequence: reserve)
let interrupted = keyboard.interrupt()
check(interrupted.count == 1, "interrupted held key is retained")
if case .keyboardPress(let press) = interrupted[0].event { check(press.releasedAt == nil, "interruption does not invent release") }
check(keyboard.consume(.keyUp, keyCode: 42, flags: [], isRepeat: false, at: Date(), reserveSequence: reserve) == nil,
      "recovery does not join a press across the gap")

let database = try ForegroundDatabase(path: CommandLine.arguments[1], captureWindows: true, captureInput: true)
let start = Date(timeIntervalSince1970: 1000)
try database.start(at: start, afkThresholdMS: 10000)
try database.appendApplicationLifecycle(.snapshot, at: start,
    app: ObservedApplication(bundleID: "test", name: "Test", pid: 123456789, launchedAt: nil))
try database.appendApplicationLifecycle(.appeared, at: start.addingTimeInterval(1),
    app: ObservedApplication(bundleID: "test", name: "Test", pid: 123456789, launchedAt: start))
let unknown = ObservedApplication(bundleID: "unknown", name: "Unknown", pid: 123456788, launchedAt: nil)
try database.appendApplicationLifecycle(.snapshot, at: start, app: unknown)
database.interruptUnverifiedProcesses()
try database.appendApplicationLifecycle(.snapshot, at: start.addingTimeInterval(2), app: unknown)
try database.appendMethod("window_title", mode: "notification", at: start)
try database.appendMethod("window_title", mode: "notification", at: start.addingTimeInterval(1))
try database.appendMethod("window_title", mode: "snapshot", at: start.addingTimeInterval(2))
try database.appendChannel("windows", available: true, reason: "sample_received", at: start)
try database.appendChannel("windows", available: false, reason: "read_failed", at: start.addingTimeInterval(2))
try database.appendChannel("windows", available: true, reason: "sample_received", at: start.addingTimeInterval(3))

let app = ObservedApplication(bundleID: "policy.test", name: "Policy", pid: 123456787, launchedAt: start)
func at(_ offset: TimeInterval) -> Date { start.addingTimeInterval(offset) }
try database.appendChannel("applications", available: true, reason: "snapshot_received", at: at(0))
let application = try database.appendApplicationLifecycle(.snapshot, at: at(0), app: app, policy: .regular)
try database.appendActivationPolicy(.regular, instance: application, at: at(0.5))
try database.appendActivationPolicy(.accessory, instance: application, at: at(1))
try database.appendActivationPolicy(.regular, instance: application, at: at(2))
try database.appendChannel("applications", available: false, reason: "session_paused", at: at(3))
database.interruptUnverifiedProcesses()
let resumed = try database.appendApplicationLifecycle(.snapshot, at: at(4), app: app, policy: .regular)
check(application == resumed, "a verified application keeps its identity across a gap")
try database.appendChannel("applications", available: true, reason: "snapshot_received", at: at(4))
try database.appendActivationPolicy(nil, instance: application, at: at(5))
try database.appendActivationPolicy(nil, instance: application, at: at(5.5))
try database.appendActivationPolicy(.regular, instance: application, at: at(6))
try database.appendApplicationLifecycle(.disappeared, at: at(7), app: app, instance: application)
let replacement = ObservedApplication(bundleID: app.bundleID, name: app.name, pid: app.pid, launchedAt: at(8))
let replaced = try database.appendApplicationLifecycle(.appeared, at: at(8), app: replacement, policy: .prohibited)
check(replaced != application, "a reused PID gets a new application identity")
try database.stop(at: at(9), failed: true)

let mediaDatabase = try ForegroundDatabase(path: CommandLine.arguments[1], captureWindows: false, captureInput: false,
                                           mediaChannels: [.microphone, .audioOutput, .camera])
try mediaDatabase.start(at: Date(), afkThresholdMS: 10000)
var listChanged: (() -> Void)?
var listeners: [MediaChannel: () -> Void] = [:]
var audioIDs: [UInt32] = [42]
var input: Bool? = false
var output = false
var inputListenerAvailable = true
var linkedApplication: String?
let audio = MediaObjectSource(
    list: {
        check(listChanged != nil, "the list is watched before the snapshot")
        return audioIDs
    },
    watchList: { callback in
        listChanged = callback
        return { listChanged = nil }
    },
    watchRestart: nil,
    identity: { _ in .audio(pid: 1234, bundleID: "", processInstanceID: nil) },
    matchApplication: { _, _, _ in linkedApplication },
    read: { _, channel in
        check(listeners[channel] != nil || !inputListenerAvailable, "properties are watched before the snapshot")
        return channel == .microphone ? input : output
    },
    watch: { _, channel, callback in
        if channel == .microphone && !inputListenerAvailable { return nil }
        listeners[channel] = callback
        return { listeners.removeValue(forKey: channel) }
    })
let audioObserver = MediaObjectObserver(source: audio, channels: [.microphone, .audioOutput], kind: .audioProcess,
                                       database: mediaDatabase, failed: { fatalError("\($0)") })
try audioObserver.reconcile()
try audioObserver.reconcile()
linkedApplication = try mediaDatabase.appendApplicationLifecycle(.appeared, at: Date(),
    app: ObservedApplication(bundleID: "fixture", name: "Fixture", pid: 1234, launchedAt: Date()))
output = true
listeners[.audioOutput]?()
listeners[.audioOutput]?()
input = true
listeners[.microphone]?()
input = nil
listeners[.microphone]?()
listeners[.microphone]?()
input = false
listeners[.microphone]?()
output = false
listeners[.audioOutput]?()
let stale = listeners[.audioOutput]
try audioObserver.interrupt(reason: "session_paused")
linkedApplication = nil
stale?()
inputListenerAvailable = false
try audioObserver.reconcile()
inputListenerAvailable = true
try audioObserver.reconcile()
audioIDs = []
listChanged?()
stale?()
audioIDs = [42]
listChanged?()
output = true
listeners[.audioOutput]?()
audioIDs = []
listChanged?()
audioObserver.stop()

var cameraIDs: [UInt32]?
var camerasChanged: (() -> Void)?
var runningChanged: (() -> Void)?
var cameraRunning = false
let camera = MediaObjectSource(
    list: { cameraIDs },
    watchList: { callback in
        camerasChanged = callback
        return { camerasChanged = nil }
    },
    watchRestart: nil,
    identity: { _ in .camera(uid: "fixture-camera", name: "Camera") },
    matchApplication: nil,
    read: { _, _ in cameraRunning },
    watch: { _, _, callback in
        runningChanged = callback
        return { runningChanged = nil }
    })
let cameraObserver = MediaObjectObserver(source: camera, channels: [.camera], kind: .cameraDevice,
                                        database: mediaDatabase, failed: { fatalError("\($0)") })
try cameraObserver.reconcile()
cameraIDs = []
try cameraObserver.reconcile()
cameraIDs = [17]
camerasChanged?()
cameraRunning = true
runningChanged?()
cameraRunning = false
runningChanged?()
cameraIDs = []
camerasChanged?()
cameraIDs = [18]
camerasChanged?()
cameraObserver.stop()
try mediaDatabase.stop(at: Date())
print("PASS: native event scheduling and recovery")
