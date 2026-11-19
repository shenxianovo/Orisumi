import Foundation
import AppKit
import SQLite3

enum EventKind: String {
    case snapshot, activated, sleep, wake
    case sessionActive = "session_active"
    case sessionInactive = "session_inactive"
}

enum AFKEventKind: String {
    case snapshot, transition
}

enum AFKState: String {
    case active, idle
}

enum CapturePermission: String {
    case accessibility
    case inputMonitoring = "input_monitoring"
}

struct CapturePermissionState {
    private var previous: Bool?
    var granted: Bool { previous ?? false }

    mutating func observe(_ granted: Bool, record: (String) throws -> Void) rethrows {
        guard granted != previous else { return }
        try record(previous == nil ? (granted ? "snapshot_granted" : "snapshot_denied") : (granted ? "restored" : "lost"))
        previous = granted
    }
}

enum WindowEventKind: String {
    case snapshot
    case focusChanged = "focus_changed"
    case titleChanged = "title_changed"
    case readFailed = "read_failed"
}

enum AppLifecycleEventKind: String {
    case snapshot, appeared, disappeared
}

enum ApplicationActivationPolicy: String {
    case regular, accessory, prohibited
}

enum MediaObjectKind: String {
    case audioProcess = "audio_process"
    case cameraDevice = "camera_device"
}

enum MediaChannel: String, CaseIterable {
    case microphone, audioOutput = "audio_output", camera

    var observations: String {
        switch self {
        case .microphone: "audio_input_observations"
        case .audioOutput: "audio_output_observations"
        case .camera: "camera_running_observations"
        }
    }

    var kind: MediaObjectKind { self == .camera ? .cameraDevice : .audioProcess }
}

enum MediaObjectIdentity {
    case audio(pid: pid_t?, bundleID: String?, processInstanceID: String?)
    case camera(uid: String?, name: String?)
}

struct ObservedApplication {
    let bundleID: String?
    let name: String?
    let pid: Int32
    let launchedAt: Date?
}

struct ForegroundApplication {
    let bundleID: String?
    let name: String?
    let pid: Int32
}

private extension EventKind {
    enum Table: String {
        case foreground = "foreground_app_events"
        case systemSleep = "system_sleep_events"
        case userSession = "user_session_events"
    }

    var table: Table {
        switch self {
        case .snapshot, .activated: .foreground
        case .sleep, .wake: .systemSleep
        case .sessionActive, .sessionInactive: .userSession
        }
    }
}

private struct DatabaseError: LocalizedError {
    let errorDescription: String?

    init(_ database: OpaquePointer?) {
        errorDescription = String(cString: sqlite3_errmsg(database))
    }

    init(message: String) {
        errorDescription = message
    }
}

// The host a run starts on, read by the .NET platform layer and passed to the helper.
struct HostObservation: Decodable {
    let platform: String
    let hardwareUuid: String?
    let hostName: String?
    let model: String?
    let osVersion: String?
    let osBuild: String?
    let bootId: String?
    let userId: Int64
    let userName: String
    let storePath: String
}

final class ForegroundDatabase {
    private let database: OpaquePointer
    private var inserts: [EventKind.Table: OpaquePointer] = [:]
    private var afkInsert: OpaquePointer?
    private var keyboardInsert: OpaquePointer?
    private var mouseInsert: OpaquePointer?
    private let runID = UUID().uuidString.lowercased()
    private var sequence: Int64 = 0
    private let host: HostObservation
    private let captureWindows: Bool
    private let captureInput: Bool
    private let mediaChannels: Set<MediaChannel>
    private var processes: [Int32: (id: String, launch: Date?)] = [:]
    private var activationPolicies: [String: ApplicationActivationPolicy?] = [:]
    private var focusID: String?
    private var channels: [String: Bool] = [:]
    private var methods: [String: String] = [:]

    init(path: String, host: HostObservation, captureWindows: Bool, captureInput: Bool, mediaChannels: Set<MediaChannel> = []) throws {
        // The file name is the database ID (storage.mdx).
        let databaseID = URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
        guard databaseID.count == 32, databaseID.allSatisfy({ "0123456789abcdef".contains($0) }) else {
            throw DatabaseError(message: "database file name is not a database ID: \(path)")
        }
        self.host = host
        self.captureWindows = captureWindows
        self.captureInput = captureInput
        self.mediaChannels = mediaChannels
        var connection: OpaquePointer?
        guard sqlite3_open(path, &connection) == SQLITE_OK, let connection else {
            let error = DatabaseError(connection)
            sqlite3_close(connection)
            throw error
        }

        do {
            guard sqlite3_busy_timeout(connection, 3000) == SQLITE_OK,
                  sqlite3_exec(connection, "PRAGMA foreign_keys = ON", nil, nil, nil) == SQLITE_OK
            else { throw DatabaseError(connection) }
            try DatabaseSchema.ensureCreated(connection, databaseID: databaseID)
            guard sqlite3_exec(connection, "PRAGMA journal_mode = WAL", nil, nil, nil) == SQLITE_OK
            else { throw DatabaseError(connection) }
        } catch {
            sqlite3_close(connection)
            throw error
        }
        database = connection
    }

    deinit {
        for statement in inserts.values { sqlite3_finalize(statement) }
        sqlite3_finalize(afkInsert)
        sqlite3_finalize(keyboardInsert)
        sqlite3_finalize(mouseInsert)
        sqlite3_close(database)
    }

    private func prepare(_ sql: String) throws -> OpaquePointer {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement
        else {
            throw DatabaseError(database)
        }
        return statement
    }

    private func eventInsert(for table: EventKind.Table) throws -> OpaquePointer {
        if let statement = inserts[table] { return statement }
        let columns = "run_id, run_sequence, observed_at_utc_ms, event_kind"
        let statement: OpaquePointer
        if table == .foreground {
            statement = try prepare("""
                INSERT INTO \(table.rawValue) (\(columns), has_app, process_instance_id)
                VALUES (?, ?, ?, ?, ?, ?)
                """)
        } else {
            statement = try prepare("INSERT INTO \(table.rawValue) (\(columns)) VALUES (?, ?, ?, ?)")
        }
        inserts[table] = statement
        return statement
    }

    private func bindText(_ statement: OpaquePointer, _ index: Int32, _ text: String?) -> Int32 {
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        return sqlite3_bind_text(statement, index, text, -1, transient)
    }

    private func timestamp(_ time: Date) -> Int64 {
        Int64(time.timeIntervalSince1970 * 1000)
    }

    private func writeRunTime(_ sql: String, at time: Date, afkThresholdMS: Int64? = nil) throws {
        let statement = try prepare(sql)
        defer { sqlite3_finalize(statement) }
        guard bindText(statement, 1, runID) == SQLITE_OK,
              sqlite3_bind_int64(statement, 2, timestamp(time)) == SQLITE_OK,
              afkThresholdMS.map({ sqlite3_bind_int64(statement, 3, $0) == SQLITE_OK }) ?? true,
              sqlite3_step(statement) == SQLITE_DONE
        else {
            throw DatabaseError(database)
        }
    }

    func start(at time: Date, afkThresholdMS: Int64) throws {
        let mediaColumns = MediaChannel.allCases.map { "capture_\($0.rawValue)" }.joined(separator: ",")
        let mediaValues = MediaChannel.allCases.map { mediaChannels.contains($0) ? "1" : "0" }.joined(separator: ",")
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        do {
            let host = try recordHost(at: time)
            try writeRunTime("""
                INSERT INTO capture_runs (run_id, started_at_utc_ms, afk_threshold_ms,capture_windows,capture_input,\(mediaColumns),host_observation_id)
                VALUES (?1, ?2, ?3,\(captureWindows ? 1 : 0),\(captureInput ? 1 : 0),\(mediaValues),\(host))
                """, at: time, afkThresholdMS: afkThresholdMS)
            guard sqlite3_exec(database, "COMMIT", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        } catch {
            sqlite3_exec(database, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    // Same rule as HostObservation.cs: a row only when one of the values changes.
    private func recordHost(at time: Date) throws -> Int64 {
        // Parameters ?1 to ?9 and ?11; user_id (?7) is the only number.
        let texts: [Int32: String?] = [
            1: host.hardwareUuid, 2: host.hostName, 3: host.model, 4: host.osVersion, 5: host.osBuild, 6: host.bootId,
            8: host.userName, 9: host.storePath, 11: host.platform,
        ]
        func bind(_ statement: OpaquePointer) throws {
            for (position, value) in texts {
                guard bindText(statement, position, value) == SQLITE_OK else { throw DatabaseError(database) }
            }
            guard sqlite3_bind_int64(statement, 7, host.userId) == SQLITE_OK else { throw DatabaseError(database) }
        }
        let latest = try prepare("""
            SELECT observation_id FROM host_observations
            WHERE observation_id = (SELECT MAX(observation_id) FROM host_observations)
              AND hardware_uuid IS ?1 AND host_name IS ?2 AND model IS ?3 AND os_version IS ?4 AND os_build IS ?5
              AND boot_id IS ?6 AND user_id IS ?7 AND user_name IS ?8 AND store_path IS ?9 AND platform IS ?11
            """)
        defer { sqlite3_finalize(latest) }
        try bind(latest)
        switch sqlite3_step(latest) {
        case SQLITE_ROW: return sqlite3_column_int64(latest, 0)
        case SQLITE_DONE: break
        default: throw DatabaseError(database)
        }
        let insert = try prepare("""
            INSERT INTO host_observations(observed_at_utc_ms,platform,hardware_uuid,host_name,model,os_version,os_build,boot_id,user_id,user_name,store_path)
            VALUES (?10,?11,?1,?2,?3,?4,?5,?6,?7,?8,?9)
            """)
        defer { sqlite3_finalize(insert) }
        try bind(insert)
        guard sqlite3_bind_int64(insert, 10, timestamp(time)) == SQLITE_OK,
              sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
        return sqlite3_last_insert_rowid(database)
    }

    func appendClock(_ kind: String, at time: Date, monotonicNS: Int64) throws {
        let insert = try prepare("""
            INSERT INTO clock_events (run_id, run_sequence, observed_at_utc_ms, event_kind, monotonic_ns)
            VALUES (?, ?, ?, ?, ?)
            """)
        defer { sqlite3_finalize(insert) }
        try bindEvent(insert, kind: kind, at: time)
        guard sqlite3_bind_int64(insert, 5, monotonicNS) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
    }

    func stop(at time: Date, failed: Bool = false) throws {
        try writeRunTime("UPDATE capture_runs SET stopped_at_utc_ms = ?2, stop_reason = '\(failed ? "failed" : "stopped")' WHERE run_id = ?1", at: time)
    }

    func appendPermission(_ permission: CapturePermission, kind: String, at time: Date) throws {
        let insert = try prepare("""
            INSERT INTO capture_permission_events (run_id, run_sequence, observed_at_utc_ms, event_kind, permission)
            VALUES (?, ?, ?, ?, ?)
            """)
        defer { sqlite3_finalize(insert) }
        try bindEvent(insert, kind: kind, at: time)
        guard bindText(insert, 5, permission.rawValue) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
    }

    func appendTimeZone(_ identifier: String, at time: Date) throws {
        let insert = try prepare("""
            INSERT INTO time_zone_events (run_id, run_sequence, observed_at_utc_ms, time_zone_id)
            VALUES (?, ?, ?, ?)
            """)
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: sequence + 1)
        guard bindText(insert, 4, identifier) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
    }

    func append(_ kind: EventKind, at time: Date, app: ForegroundApplication? = nil) throws {
        let insert = try eventInsert(for: kind.table)
        defer { sqlite3_reset(insert) }
        try bindEvent(insert, kind: kind.rawValue, at: time)
        if kind.table == .foreground {
            let instance = try app.map { try processInstance(pid: $0.pid, bundle: $0.bundleID, name: $0.name) }
            guard sqlite3_bind_int(insert, 5, instance == nil ? 0 : 1) == SQLITE_OK,
                  bindText(insert, 6, instance) == SQLITE_OK else { throw DatabaseError(database) }
        }
        try commitEvent(insert)
    }

    func appendAFK(_ kind: AFKEventKind, at time: Date, state: AFKState, idleMS: Int64) throws {
        if afkInsert == nil {
            afkInsert = try prepare("""
                INSERT INTO afk_events (run_id, run_sequence, observed_at_utc_ms, event_kind, state, idle_ms)
                VALUES (?, ?, ?, ?, ?, ?)
                """)
        }
        let insert = afkInsert!
        defer { sqlite3_reset(insert) }
        try bindEvent(insert, kind: kind.rawValue, at: time)
        guard bindText(insert, 5, state.rawValue) == SQLITE_OK,
              sqlite3_bind_int64(insert, 6, idleMS) == SQLITE_OK
        else {
            throw DatabaseError(database)
        }
        try commitEvent(insert)
    }

    private func processInstance(pid: Int32, bundle: String? = nil, name: String? = nil, launch: Date? = nil) throws -> String {
        let app = NSRunningApplication(processIdentifier: pid)
        let launched = launch ?? app?.launchDate
        if let known = processes[pid], known.launch == launched { return known.id }
        let id = UUID().uuidString.lowercased()
        let insert = try prepare("INSERT INTO process_instances VALUES(?,?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        guard bindText(insert, 1, id) == SQLITE_OK, bindText(insert, 2, runID) == SQLITE_OK,
              sqlite3_bind_int(insert, 3, pid) == SQLITE_OK,
              bindText(insert, 4, bundle ?? app?.bundleIdentifier) == SQLITE_OK,
              bindText(insert, 5, name ?? app?.localizedName) == SQLITE_OK,
              bindOptionalInteger(insert, 6, launched.map(timestamp)) == SQLITE_OK,
              sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
        processes[pid] = (id, launched)
        return id
    }

    func interruptUnverifiedProcesses() {
        processes = processes.filter { $0.value.launch != nil }
        activationPolicies.removeAll()
    }

    func appendChannel(_ channel: String, available: Bool, reason: String, at time: Date = Date()) throws {
        guard channels[channel] != available else { return }
        let insert = try prepare("INSERT INTO capture_channel_events VALUES(?,?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: sequence + 1)
        guard bindText(insert, 4, channel) == SQLITE_OK,
              sqlite3_bind_int(insert, 5, available ? 1 : 0) == SQLITE_OK,
              bindText(insert, 6, reason) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
        channels[channel] = available
    }

    func appendMethod(_ channel: String, mode: String, at time: Date = Date()) throws {
        guard methods[channel] != mode else { return }
        let insert = try prepare("INSERT INTO capture_method_events VALUES(?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: sequence + 1)
        guard bindText(insert, 4, channel) == SQLITE_OK,
              bindText(insert, 5, mode) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
        methods[channel] = mode
    }

    /// `hasWindow` is nil when the focused window could not be read.
    func appendWindow(_ kind: WindowEventKind, at time: Date, pid: pid_t, title: String?, hasWindow: Bool?) throws {
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        var committed = false
        defer { if !committed { sqlite3_exec(database, "ROLLBACK", nil, nil, nil) } }
        if kind != .titleChanged {
            focusID = hasWindow == true ? UUID().uuidString.lowercased() : nil
            if let focusID {
                let instance = try processInstance(pid: pid)
                let insert = try prepare("INSERT INTO window_focus_instances VALUES(?,?,?)")
                defer { sqlite3_finalize(insert) }
                guard bindText(insert, 1, focusID) == SQLITE_OK, bindText(insert, 2, runID) == SQLITE_OK,
                      bindText(insert, 3, instance) == SQLITE_OK, sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
            }
            let insert = try prepare("INSERT INTO focused_window_events VALUES(?,?,?,?,?,?)")
            defer { sqlite3_finalize(insert) }
            try bindEvent(insert, kind: kind.rawValue, at: time)
            guard bindOptionalInteger(insert, 5, hasWindow.map { $0 ? 1 : 0 }) == SQLITE_OK,
                  bindText(insert, 6, focusID) == SQLITE_OK else { throw DatabaseError(database) }
            try commitEvent(insert)
        }
        if let focusID {
            let insert = try prepare("INSERT INTO window_title_observations VALUES(?,?,?,?,?)")
            defer { sqlite3_finalize(insert) }
            try bindObservation(insert, at: time, runSequence: kind == .titleChanged ? sequence + 1 : sequence)
            guard bindText(insert, 4, focusID) == SQLITE_OK, bindText(insert, 5, title) == SQLITE_OK else { throw DatabaseError(database) }
            guard sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
            if kind == .titleChanged { sequence += 1 }
        }
        guard sqlite3_exec(database, "COMMIT", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        committed = true
    }

    @discardableResult
    func appendApplicationLifecycle(_ kind: AppLifecycleEventKind, at time: Date, app: ObservedApplication,
                                    policy: ApplicationActivationPolicy? = nil, instance existing: String? = nil) throws -> String {
        let previous = processes[app.pid]
        let instance: String
        do {
            instance = try transaction {
                let instance = try existing ?? processInstance(pid: app.pid, bundle: app.bundleID, name: app.name, launch: app.launchedAt)
                let insert = try prepare("INSERT INTO running_app_observations VALUES(?,?,?,?,?)")
                defer { sqlite3_finalize(insert) }
                try bindEvent(insert, kind: kind.rawValue, at: time)
                guard bindText(insert, 5, instance) == SQLITE_OK else { throw DatabaseError(database) }
                try commitEvent(insert)
                if kind != .disappeared {
                    try writeActivationPolicy(policy, instance: instance, at: time, runSequence: sequence)
                }
                return instance
            }
        } catch {
            processes[app.pid] = previous
            throw error
        }
        if kind == .disappeared {
            if processes[app.pid]?.id == instance { processes.removeValue(forKey: app.pid) }
            activationPolicies.removeValue(forKey: instance)
        } else {
            activationPolicies[instance] = .some(policy)
        }
        return instance
    }

    func appendActivationPolicy(_ policy: ApplicationActivationPolicy?, instance: String, at time: Date) throws {
        guard activationPolicies[instance] != .some(policy) else { return }
        try writeActivationPolicy(policy, instance: instance, at: time, runSequence: sequence + 1)
        sequence += 1
        activationPolicies[instance] = .some(policy)
    }

    private func writeActivationPolicy(_ policy: ApplicationActivationPolicy?, instance: String,
                                       at time: Date, runSequence: Int64) throws {
        let insert = try prepare("INSERT INTO application_activation_policy_observations VALUES(?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: runSequence)
        guard bindText(insert, 4, instance) == SQLITE_OK,
              bindText(insert, 5, policy?.rawValue) == SQLITE_OK,
              sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
    }

    func appendMediaObject(_ kind: MediaObjectKind, objectID: UInt32, identity: MediaObjectIdentity,
                           event: AppLifecycleEventKind, values: [MediaChannel: Bool?], at time: Date) throws -> String {
        try transaction {
            let instance = try mediaInstance(identity, objectID: objectID)
            try appendMediaPresence(event, kind: kind, instance: instance, objectID: objectID, at: time)
            for (channel, value) in values {
                try writeMediaState(value, channel: channel, instance: instance, at: time, runSequence: sequence)
            }
            return instance
        }
    }

    func appendMediaDisappearance(_ kind: MediaObjectKind, instance: String, objectID: UInt32,
                                  stopped: [MediaChannel], at time: Date) throws {
        try transaction {
            try appendMediaPresence(.disappeared, kind: kind, instance: instance, objectID: objectID, at: time)
            for channel in stopped {
                try writeMediaState(false, channel: channel, instance: instance, at: time, runSequence: sequence)
            }
        }
    }

    private func appendMediaPresence(_ event: AppLifecycleEventKind, kind: MediaObjectKind, instance: String,
                                     objectID: UInt32, at time: Date) throws {
        let parameters = kind == .cameraDevice ? "?,?,?,?,?,?" : "?,?,?,?,?"
        let insert = try prepare("INSERT INTO \(kind.rawValue)_presence_events VALUES(\(parameters))")
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: sequence + 1)
        guard bindText(insert, 4, instance) == SQLITE_OK,
              bindText(insert, 5, event.rawValue) == SQLITE_OK else { throw DatabaseError(database) }
        if kind == .cameraDevice {
            guard sqlite3_bind_int64(insert, 6, Int64(objectID)) == SQLITE_OK else { throw DatabaseError(database) }
        }
        try commitEvent(insert)
    }

    func appendMediaState(_ value: Bool?, channel: MediaChannel, instance: String, at time: Date) throws {
        try writeMediaState(value, channel: channel, instance: instance, at: time, runSequence: sequence + 1)
        sequence += 1
    }

    func linkAudioProcess(instance: String, application: String) throws {
        let update = try prepare("""
            UPDATE audio_process_instances SET process_instance_id = ?
            WHERE audio_process_instance_id = ? AND process_instance_id IS NULL
            """)
        defer { sqlite3_finalize(update) }
        guard bindText(update, 1, application) == SQLITE_OK, bindText(update, 2, instance) == SQLITE_OK,
              sqlite3_step(update) == SQLITE_DONE else { throw DatabaseError(database) }
    }

    private func writeMediaState(_ value: Bool?, channel: MediaChannel, instance: String,
                                 at time: Date, runSequence: Int64) throws {
        let insert = try prepare("INSERT INTO \(channel.observations) VALUES(?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        try bindObservation(insert, at: time, runSequence: runSequence)
        guard bindText(insert, 4, instance) == SQLITE_OK,
              bindOptionalInteger(insert, 5, value.map { $0 ? 1 : 0 }) == SQLITE_OK,
              sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
    }

    private func mediaInstance(_ identity: MediaObjectIdentity, objectID: UInt32) throws -> String {
        let instance = UUID().uuidString.lowercased()
        switch identity {
        case let .audio(pid, bundleID, processInstanceID):
            let insert = try prepare("INSERT INTO audio_process_instances VALUES(?,?,?,?,?,?)")
            defer { sqlite3_finalize(insert) }
            guard bindText(insert, 1, instance) == SQLITE_OK, bindText(insert, 2, runID) == SQLITE_OK,
                  sqlite3_bind_int64(insert, 3, Int64(objectID)) == SQLITE_OK,
                  bindOptionalInteger(insert, 4, pid.map(Int64.init)) == SQLITE_OK,
                  bindText(insert, 5, bundleID) == SQLITE_OK,
                  bindText(insert, 6, processInstanceID) == SQLITE_OK,
                  sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
            return instance
        case let .camera(uid, name):
            let insert = try prepare("""
                INSERT INTO camera_devices VALUES(?,?,?)
                ON CONFLICT(device_uid) DO UPDATE SET device_name = COALESCE(camera_devices.device_name, excluded.device_name)
                RETURNING camera_device_id
                """)
            defer { sqlite3_finalize(insert) }
            guard bindText(insert, 1, instance) == SQLITE_OK,
                  bindText(insert, 2, uid) == SQLITE_OK,
                  bindText(insert, 3, name) == SQLITE_OK,
                  sqlite3_step(insert) == SQLITE_ROW else { throw DatabaseError(database) }
            let device = String(cString: sqlite3_column_text(insert, 0))
            guard sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
            return device
        }
    }

    private func transaction<Result>(_ write: () throws -> Result) throws -> Result {
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        let previousSequence = sequence
        do {
            let result = try write()
            guard sqlite3_exec(database, "COMMIT", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
            return result
        } catch {
            sqlite3_exec(database, "ROLLBACK", nil, nil, nil)
            sequence = previousSequence
            throw error
        }
    }

    func reserveInputSequence() -> Int64 {
        sequence += 1
        return sequence
    }

    func appendInputBatch(_ observations: [InputObservation]) throws {
        guard !observations.isEmpty else { return }
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else {
            throw DatabaseError(database)
        }
        do {
            for observation in observations {
                try appendInput(observation)
            }
            guard sqlite3_exec(database, "COMMIT", nil, nil, nil) == SQLITE_OK else {
                throw DatabaseError(database)
            }
        } catch {
            sqlite3_exec(database, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    private func appendInput(_ observation: InputObservation) throws {
        switch observation.event {
        case let .keyboardPress(press):
            if keyboardInsert == nil {
                keyboardInsert = try prepare("""
                    INSERT INTO keyboard_presses
                        (run_id, run_sequence, pressed_at_utc_ms, released_at_utc_ms, key_code, modifiers_at_press)
                    VALUES (?, ?, ?, ?, ?, ?)
                    """)
            }
            let insert = keyboardInsert!
            defer { sqlite3_reset(insert) }
            try bindObservation(insert, at: observation.observedAt, runSequence: observation.runSequence)
            guard bindOptionalInteger(insert, 4, press.releasedAt.map(timestamp)) == SQLITE_OK,
                  sqlite3_bind_int64(insert, 5, press.keyCode) == SQLITE_OK,
                  sqlite3_bind_int64(insert, 6, press.modifiersAtPress) == SQLITE_OK,
                  sqlite3_step(insert) == SQLITE_DONE
            else {
                throw DatabaseError(database)
            }
        case let .mouse(kind, xPoints, yPoints, button):
            if mouseInsert == nil {
                mouseInsert = try prepare("""
                    INSERT INTO mouse_button_events
                        (run_id, run_sequence, observed_at_utc_ms, event_kind, x_points, y_points, button)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    """)
            }
            let insert = mouseInsert!
            defer { sqlite3_reset(insert) }
            try bindEvent(insert, kind: kind, at: observation.observedAt, runSequence: observation.runSequence)
            guard sqlite3_bind_double(insert, 5, xPoints) == SQLITE_OK,
                  sqlite3_bind_double(insert, 6, yPoints) == SQLITE_OK,
                  sqlite3_bind_int64(insert, 7, button) == SQLITE_OK,
                  sqlite3_step(insert) == SQLITE_DONE
            else {
                throw DatabaseError(database)
            }
        }
    }

    private func bindOptionalInteger(_ statement: OpaquePointer, _ index: Int32, _ value: Int64?) -> Int32 {
        value.map { sqlite3_bind_int64(statement, index, $0) } ?? sqlite3_bind_null(statement, index)
    }

    private func bindEvent(_ insert: OpaquePointer, kind: String, at time: Date, runSequence: Int64? = nil) throws {
        try bindObservation(insert, at: time, runSequence: runSequence ?? sequence + 1)
        guard bindText(insert, 4, kind) == SQLITE_OK else { throw DatabaseError(database) }
    }

    private func bindObservation(_ insert: OpaquePointer, at time: Date, runSequence: Int64) throws {
        guard bindText(insert, 1, runID) == SQLITE_OK,
              sqlite3_bind_int64(insert, 2, runSequence) == SQLITE_OK,
              sqlite3_bind_int64(insert, 3, timestamp(time)) == SQLITE_OK
        else {
            throw DatabaseError(database)
        }
    }

    private func commitEvent(_ insert: OpaquePointer) throws {
        guard sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
        sequence += 1
    }
}
