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
try database.stop(at: start.addingTimeInterval(3), failed: true)
print("PASS: native event scheduling and recovery")
