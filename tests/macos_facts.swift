import Foundation
import Darwin

// The host the .NET platform layer would pass; the helper records it as given.
let host = HostObservation(platform: "macos", hardwareUuid: "00000000-0000-0000-0000-000000000001", hostName: "fixture",
                           model: nil, osVersion: nil, osBuild: nil, bootId: "fixture-boot", userId: 501, userName: "fixture",
                           storePath: "/fixture/store")
let path = CommandLine.arguments[1]
let directory = URL(fileURLWithPath: path).deletingLastPathComponent()
if CommandLine.arguments.contains("reject") {
    do {
        _ = try ForegroundDatabase(path: path, host: host, captureWindows: false, captureInput: false)
        fatalError("accepted an invalid schema")
    } catch { print("Rejected schema: \(error.localizedDescription)") }
    exit(0)
}
do {
    _ = try ForegroundDatabase(path: directory.appendingPathComponent("macos.sqlite").path, host: host, captureWindows: false, captureInput: false)
    fatalError("accepted a file name that is not a database ID")
} catch { print("Rejected file name: \(error.localizedDescription)") }
for _ in 0..<2 {
    let disabled = try ForegroundDatabase(path: directory.appendingPathComponent(String(repeating: "d", count: 32) + ".sqlite").path,
                                          host: host, captureWindows: false, captureInput: false)
    try disabled.start(at: Date(), afkThresholdMS: 300000)
    try disabled.stop(at: Date())
}
let db = try ForegroundDatabase(path: path, host: host, captureWindows: true, captureInput: true)
let now = Date()
try db.start(at: now, afkThresholdMS: 300000)
let app = ObservedApplication(bundleID: "test.app", name: "Test", pid: 123456, launchedAt: now)
try db.appendApplicationLifecycle(.snapshot, at: now, app: app)
try db.append(.snapshot, at: now, app: ForegroundApplication(bundleID: "test.app", name: "Test", pid: 123456))
try db.appendWindow(.snapshot, at: now, pid: 123456, title: "A", hasWindow: true)
try db.appendWindow(.titleChanged, at: now.addingTimeInterval(1), pid: 123456, title: "B", hasWindow: true)
try db.appendWindow(.focusChanged, at: now.addingTimeInterval(2), pid: 123456, title: "C", hasWindow: true)
try db.appendWindow(.focusChanged, at: now.addingTimeInterval(3), pid: 123456, title: nil, hasWindow: false)
try db.appendWindow(.readFailed, at: now.addingTimeInterval(3.2), pid: 123456, title: nil, hasWindow: nil)
try db.appendWindow(.snapshot, at: now.addingTimeInterval(3.4), pid: 123456, title: "", hasWindow: true)
try db.append(.activated, at: now.addingTimeInterval(3.6), app: nil)
try db.appendApplicationLifecycle(.disappeared, at: now.addingTimeInterval(4), app: app)
try db.appendApplicationLifecycle(.appeared, at: now.addingTimeInterval(5), app: ObservedApplication(bundleID: "other.app", name: "Other", pid: 123456, launchedAt: now.addingTimeInterval(5)))
try db.appendChannel("input", available: true, reason: "started", at: now)
try db.appendChannel("input", available: true, reason: "unchanged", at: now)
try db.appendChannel("input", available: false, reason: "interrupted", at: now)
try db.appendChannel("input", available: true, reason: "resumed", at: now)
try db.stop(at: now.addingTimeInterval(6))
