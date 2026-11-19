import Foundation

if CommandLine.arguments.contains("backpressure") {
    let pipe = ObservationPipe()
    do {
        let batch = Data(repeating: 65, count: ObservationPipe.capacity / 2)
        for _ in 0..<3 { try pipe.send(batch) }
        fatalError("accepted more bytes than the queue limit with an unread pipe")
    } catch {
        fputs("\(error.localizedDescription)\n", stderr)
        exit(1)
    }
}

let db = ObservationEvents { FileHandle.standardOutput.write($0) }
let now = Date()
let app = ObservedApplication(bundleID: "test.app", name: "Test", pid: 123456, launchedAt: now)
let instance = try db.appendApplicationLifecycle(.snapshot, at: now, app: app)
try db.append(.snapshot, at: now, instance: instance)
try db.appendWindow(.snapshot, at: now, instance: instance, title: "A", hasWindow: true)
try db.appendWindow(.titleChanged, at: now.addingTimeInterval(1), title: "B", hasWindow: true)
try db.appendWindow(.focusChanged, at: now.addingTimeInterval(2), instance: instance, title: "C", hasWindow: true)
try db.appendWindow(.focusChanged, at: now.addingTimeInterval(3), title: nil, hasWindow: false)
try db.appendWindow(.readFailed, at: now.addingTimeInterval(3.2), title: nil, hasWindow: nil)
try db.appendWindow(.snapshot, at: now.addingTimeInterval(3.4), instance: instance, title: "", hasWindow: true)
try db.append(.activated, at: now.addingTimeInterval(3.6))
try db.appendApplicationLifecycle(.disappeared, at: now.addingTimeInterval(4), app: app, instance: instance)
try db.appendApplicationLifecycle(.appeared, at: now.addingTimeInterval(5), app: ObservedApplication(bundleID: "other.app", name: "Other", pid: 123456, launchedAt: now.addingTimeInterval(5)))
try db.appendChannel("input", available: true, reason: "started", at: now)
try db.appendChannel("input", available: true, reason: "unchanged", at: now)
try db.appendChannel("input", available: false, reason: "interrupted", at: now)
try db.appendChannel("input", available: true, reason: "resumed", at: now)
let observationTime = now.addingTimeInterval(5.5)
let key = try db.reserveInput(at: observationTime)
let mouse = try db.reserveInput(at: observationTime.addingTimeInterval(0.2))
try db.flush(at: observationTime.addingTimeInterval(0.3))
try db.appendInputBatch([
    InputObservation(token: mouse, observedAt: observationTime.addingTimeInterval(0.2),
                     event: .mouse(kind: "button_down", xPoints: -42.25, yPoints: 314.5, button: 0)),
    InputObservation(token: key, observedAt: observationTime,
                     event: .keyboardPress(KeyboardPress(keyCode: 42, modifiersAtPress: 131072, releasedAt: nil))),
])
try db.flush(at: now.addingTimeInterval(6), stopped: true)
