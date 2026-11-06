import AppKit
import Darwin
import Foundation

// 终端的 Ctrl+C 同时送到父子进程；父进程通过 stdin 请求收尾。
signal(SIGINT, SIG_IGN)

let workspace = NSWorkspace.shared
let run = UUID().uuidString
let formatter = ISO8601DateFormatter()
formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
var sequence = 0
var stopped = false
var observers: [NSObjectProtocol] = []

func application(_ app: NSRunningApplication?) -> Any {
    guard let app else { return NSNull() }
    return [
        "localizedName": app.localizedName as Any? ?? NSNull(),
        "bundleIdentifier": app.bundleIdentifier as Any? ?? NSNull(),
        "bundleURL": app.bundleURL?.absoluteString as Any? ?? NSNull(),
        "executableURL": app.executableURL?.absoluteString as Any? ?? NSNull(),
        "processIdentifier": app.processIdentifier,
        "launchDateUnixSeconds": app.launchDate?.timeIntervalSince1970 as Any? ?? NSNull(),
        "isActive": app.isActive,
        "isHidden": app.isHidden,
        "isTerminated": app.isTerminated,
        "isFinishedLaunching": app.isFinishedLaunching,
        "ownsMenuBar": app.ownsMenuBar,
        "activationPolicy": app.activationPolicy.rawValue,
        "executableArchitecture": app.executableArchitecture,
    ] as [String: Any]
}

func record(_ kind: String, _ payload: [String: Any], observedAt: Date = Date()) {
    do {
        sequence += 1
        var value = payload
        value["format"] = "orisumi-foreground-v1"
        value["run"] = run
        value["sequence"] = sequence
        value["observedAt"] = formatter.string(from: observedAt)
        value["kind"] = kind
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        try FileHandle.standardOutput.write(contentsOf: data + Data([10]))
    } catch {
        FileHandle.standardError.write(Data("前台观察输出失败：\(error)\n".utf8))
        exit(1)
    }
}

func snapshot(_ reason: String) {
    let observedAt = Date()
    record("snapshot", [
        "method": "NSWorkspace.frontmostApplication",
        "reason": reason,
        "application": application(workspace.frontmostApplication),
    ], observedAt: observedAt)
}

let names: [Notification.Name] = [
    NSWorkspace.didActivateApplicationNotification,
    NSWorkspace.didDeactivateApplicationNotification,
    NSWorkspace.willSleepNotification,
    NSWorkspace.didWakeNotification,
    NSWorkspace.sessionDidBecomeActiveNotification,
    NSWorkspace.sessionDidResignActiveNotification,
]

record("started", [
    "method": "NSWorkspace.notificationCenter",
    "hostName": ProcessInfo.processInfo.hostName,
    "osVersion": ProcessInfo.processInfo.operatingSystemVersionString,
    "observerProcessIdentifier": ProcessInfo.processInfo.processIdentifier,
    "subscriptions": names.map(\.rawValue),
])
observers = names.map { name in
    workspace.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { notification in
        guard !stopped else { return }
        let observedAt = Date()
        var info: [String: Any] = [:]
        var unknownTypes: [String: String] = [:]
        for (key, value) in notification.userInfo ?? [:] {
            let key = String(describing: key)
            if let app = value as? NSRunningApplication {
                info[key] = application(app)
            } else if JSONSerialization.isValidJSONObject(["value": value]) {
                info[key] = value
            } else {
                unknownTypes[key] = String(reflecting: type(of: value))
            }
        }
        record("notification", [
            "method": "NSWorkspace.notificationCenter",
            "notificationName": notification.name.rawValue,
            "userInfo": notification.userInfo == nil ? NSNull() : info,
            "unencodedUserInfoTypes": unknownTypes,
        ], observedAt: observedAt)
        if notification.name == NSWorkspace.didWakeNotification || notification.name == NSWorkspace.sessionDidBecomeActiveNotification {
            snapshot(notification.name.rawValue)
        }
    }
}
snapshot("initial")

DispatchQueue.global().async {
    let command = readLine()
    DispatchQueue.main.async {
        stopped = true
        for observer in observers { workspace.notificationCenter.removeObserver(observer) }
        let reason = command == "stop" ? "stop-requested" : command == nil ? "input-closed" : "invalid-command"
        record("stopped", ["reason": reason])
        exit(command == nil || command == "stop" ? 0 : 1)
    }
}
RunLoop.main.run()
