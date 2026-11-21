import Foundation
import CoreAudio
import CoreMediaIO
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
_ = keyboard.consume(.keyDown, keyCode: 42, flags: [], isRepeat: false, at: Date(), reserve: reserve)
let interrupted = keyboard.interrupt()
check(interrupted.count == 1, "interrupted held key is retained")
if case .keyboardPress(let press) = interrupted[0].event { check(press.releasedAt == nil, "interruption does not invent release") }
check(keyboard.consume(.keyUp, keyCode: 42, flags: [], isRepeat: false, at: Date(), reserve: reserve) == nil,
      "recovery does not join a press across the gap")

let events = ObservationEvents { FileHandle.standardOutput.write($0) }
if CommandLine.arguments.contains("applications") {
    let start = Date(timeIntervalSince1970: 1000)
    try events.appendApplicationLifecycle(.snapshot, at: start,
        app: ObservedApplication(bundleID: "test", name: "Test", pid: 123456789, launchedAt: nil))
    try events.appendApplicationLifecycle(.appeared, at: start.addingTimeInterval(1),
        app: ObservedApplication(bundleID: "test", name: "Test", pid: 123456789, launchedAt: start))
    let unknown = ObservedApplication(bundleID: "unknown", name: "Unknown", pid: 123456788, launchedAt: nil)
    try events.appendApplicationLifecycle(.snapshot, at: start, app: unknown)
    events.interruptUnverifiedProcesses()
    try events.appendApplicationLifecycle(.snapshot, at: start.addingTimeInterval(2), app: unknown)
    try events.appendMethod("window_title", mode: "notification", at: start)
    try events.appendMethod("window_title", mode: "notification", at: start.addingTimeInterval(1))
    try events.appendMethod("window_title", mode: "snapshot", at: start.addingTimeInterval(2))
    try events.appendChannel("windows", available: true, reason: "sample_received", at: start)
    try events.appendChannel("windows", available: false, reason: "read_failed", at: start.addingTimeInterval(2))
    try events.appendChannel("windows", available: true, reason: "sample_received", at: start.addingTimeInterval(3))

    let app = ObservedApplication(bundleID: "policy.test", name: "Policy", pid: 123456787, launchedAt: start)
    func at(_ offset: TimeInterval) -> Date { start.addingTimeInterval(offset) }
    try events.appendChannel("applications", available: true, reason: "snapshot_received", at: at(0))
    let application = try events.appendApplicationLifecycle(.snapshot, at: at(0), app: app, policy: .regular)
    try events.appendActivationPolicy(.regular, instance: application, at: at(0.5))
    try events.appendActivationPolicy(.accessory, instance: application, at: at(1))
    try events.appendActivationPolicy(.regular, instance: application, at: at(2))
    try events.appendChannel("applications", available: false, reason: "session_paused", at: at(3))
    events.interruptUnverifiedProcesses()
    let resumed = try events.appendApplicationLifecycle(.snapshot, at: at(4), app: app, policy: .regular)
    check(application == resumed, "a verified application keeps its identity across a gap")
    try events.appendChannel("applications", available: true, reason: "snapshot_received", at: at(4))
    try events.appendActivationPolicy(nil, instance: application, at: at(5))
    try events.appendActivationPolicy(nil, instance: application, at: at(5.5))
    try events.appendActivationPolicy(.regular, instance: application, at: at(6))
    try events.appendApplicationLifecycle(.disappeared, at: at(7), app: app, instance: application)
    let replacement = ObservedApplication(bundleID: app.bundleID, name: app.name, pid: app.pid, launchedAt: at(8))
    let replaced = try events.appendApplicationLifecycle(.appeared, at: at(8), app: replacement, policy: .prohibited)
    check(replaced != application, "a reused PID gets a new application identity")
    try events.flush(at: at(9))
    FileHandle.standardOutput.write(Data("{\"events\":[".utf8))
    exit(1)
}
var listChanged: (() -> Void)?
var listeners: [MediaChannel: () -> Void] = [:]
var audioIDs: [UInt32]? = [42]
var input: Result<Bool, MediaPropertyReadFailure> = .success(false)
var output: Result<Bool, MediaPropertyReadFailure> = .success(false)
let inputFailure: Result<Bool, MediaPropertyReadFailure> = .failure(.init(
    selector: kAudioProcessPropertyIsRunningInput, status: kAudioHardwareBadObjectError))
let outputFailure: Result<Bool, MediaPropertyReadFailure> = .failure(.init(
    selector: kAudioProcessPropertyIsRunningOutput, status: -50))
var audioListReads = 0
var inputListenerAvailable = true
var linkedApplication: String?
let audio = MediaObjectSource(
    list: {
        check(listChanged != nil, "the list is watched before the snapshot")
        audioListReads += 1
        return audioIDs
    },
    watchList: { callback in
        listChanged = callback
        return { listChanged = nil }
    },
    watchRestart: nil,
    identity: { _ in .audio(pid: 1234, bundleID: "com.google.Chrome.helper") },
    matchApplication: { _, pid, bundle in
        AudioResponsibility.application(for: pid, bundle: bundle, responsiblePID: { _ in 1235 }) { owner, ownerBundle in
            check(owner == 1235 && ownerBundle == nil, "the helper bundle is not the Chrome bundle")
            return linkedApplication
        }
    },
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
                                       events: events, failed: { fatalError("\($0)") })
try audioObserver.reconcile()
try audioObserver.reconcile()
linkedApplication = try events.appendApplicationLifecycle(.appeared, at: Date(),
    app: ObservedApplication(bundleID: "com.google.Chrome", name: "Chrome", pid: 1235, launchedAt: Date()), policy: .regular)
try audioObserver.reconcile()
output = .success(true)
listeners[.audioOutput]?()
listeners[.audioOutput]?()
input = .success(true)
listeners[.microphone]?()
input = inputFailure
listeners[.microphone]?()
listeners[.microphone]?()
input = .success(false)
listeners[.microphone]?()
audioIDs = nil
output = outputFailure
let beforeUnreadableList = audioListReads
listeners[.audioOutput]?()
check(audioListReads == beforeUnreadableList + 1, "a failed property read checks the list once")
audioIDs = [42]
output = .success(false)
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
audioIDs = []
input = inputFailure
let beforeVanishedClient = audioListReads
listeners[.microphone]?()
check(audioListReads == beforeVanishedClient + 1 && listeners[.microphone] == nil,
      "a vanished client is removed before the list notification")
listChanged?()
input = .success(false)
audioObserver.stop()

check(AudioResponsibility.resolve(1234, using: nil) == 1234, "missing symbol falls back to the client PID")
check(AudioResponsibility.resolve(1234, using: { _ in -1 }) == 1234, "EPERM falls back to the client PID")
check(AudioResponsibility.application(for: 1234, bundle: "com.apple.Terminal", responsiblePID: { $0 }) { pid, bundle in
    pid == 1234 && bundle == "com.apple.Terminal" ? "direct" : nil
} == "direct", "direct attribution checks the original bundle")

// An output-only run tracks clients in memory until a responsible application is observed.
audioIDs = [42]
output = .success(true)
let outputOnly = MediaObjectObserver(source: audio, channels: [.audioOutput], kind: .audioProcess,
                                    events: events, failed: { fatalError("\($0)") })
try outputOnly.start()
check(listeners[.audioOutput] == nil, "unattributed output has no listener")
audioIDs = []
listChanged?()
audioIDs = [42]
listChanged?()
linkedApplication = try events.appendApplicationLifecycle(.appeared, at: Date(),
    app: ObservedApplication(bundleID: "com.google.Chrome", name: "Chrome", pid: 1235, launchedAt: Date()), policy: .regular)
try outputOnly.reconcile()
check(listeners[.audioOutput] != nil, "output starts when its owner is recorded")
audioIDs = []
listChanged?()
outputOnly.stop()

var cameraIDs: [UInt32]?
var camerasChanged: (() -> Void)?
var cameraListeners: [UInt32: () -> Void] = [:]
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
    read: { id, _ in
        if id == 19 {
            cameraIDs = [18]
            return .failure(.init(selector: UInt32(kCMIODevicePropertyDeviceIsRunningSomewhere), status: -50))
        }
        return .success(cameraRunning)
    },
    watch: { id, _, callback in
        cameraListeners[id] = callback
        return { cameraListeners.removeValue(forKey: id) }
    })
let cameraObserver = MediaObjectObserver(source: camera, channels: [.camera], kind: .cameraDevice,
                                        events: events, failed: { fatalError("\($0)") })
try cameraObserver.reconcile()
cameraIDs = []
try cameraObserver.reconcile()
cameraIDs = [17]
camerasChanged?()
cameraRunning = true
cameraListeners[17]?()
cameraRunning = false
cameraListeners[17]?()
cameraIDs = []
camerasChanged?()
cameraIDs = [18, 19]
camerasChanged?()
check(cameraListeners[18] != nil && cameraListeners[19] == nil, "a camera that vanishes during its first read is discarded")
cameraObserver.stop()
try events.flush(stopped: true)
fputs("PASS: native event scheduling and recovery\n", stderr)
