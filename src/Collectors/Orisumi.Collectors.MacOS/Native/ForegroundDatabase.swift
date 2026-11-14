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
}

enum AppLifecycleEventKind: String {
    case snapshot, appeared, disappeared
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
}

final class ForegroundDatabase {
    private let database: OpaquePointer
    private var inserts: [EventKind.Table: OpaquePointer] = [:]
    private var afkInsert: OpaquePointer?
    private var keyboardInsert: OpaquePointer?
    private var mouseInsert: OpaquePointer?
    private let runID = UUID().uuidString.lowercased()
    private var sequence: Int64 = 0
    private let captureWindows: Bool
    private let captureInput: Bool
    private var processes: [Int32: (id: String, launch: Date?)] = [:]
    private var focusID: String?
    private var channels: [String: Bool] = [:]
    private var methods: [String: String] = [:]

    init(path: String, captureWindows: Bool, captureInput: Bool) throws {
        self.captureWindows = captureWindows
        self.captureInput = captureInput
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
            try DatabaseSchema.ensureCreated(connection)
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
                INSERT INTO \(table.rawValue) (\(columns), process_instance_id)
                VALUES (?, ?, ?, ?, ?)
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
        try writeRunTime("""
            INSERT INTO capture_runs (run_id, started_at_utc_ms, afk_threshold_ms,capture_windows,capture_input) VALUES (?1, ?2, ?3,\(captureWindows ? 1 : 0),\(captureInput ? 1 : 0))
            """, at: time, afkThresholdMS: afkThresholdMS)
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
            guard bindText(insert, 5, instance) == SQLITE_OK else { throw DatabaseError(database) }
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

    func appendWindow(_ kind: WindowEventKind, at time: Date, pid: pid_t, title: String?, hasWindow: Bool) throws {
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else { throw DatabaseError(database) }
        var committed = false
        defer { if !committed { sqlite3_exec(database, "ROLLBACK", nil, nil, nil) } }
        if kind != .titleChanged {
            focusID = hasWindow ? UUID().uuidString.lowercased() : nil
            if let focusID {
                let instance = try processInstance(pid: pid)
                let insert = try prepare("INSERT INTO window_focus_instances VALUES(?,?,?)")
                defer { sqlite3_finalize(insert) }
                guard bindText(insert, 1, focusID) == SQLITE_OK, bindText(insert, 2, runID) == SQLITE_OK,
                      bindText(insert, 3, instance) == SQLITE_OK, sqlite3_step(insert) == SQLITE_DONE else { throw DatabaseError(database) }
            }
            let insert = try prepare("INSERT INTO focused_window_events VALUES(?,?,?,?,?)")
            defer { sqlite3_finalize(insert) }
            try bindEvent(insert, kind: kind.rawValue, at: time)
            guard bindText(insert, 5, focusID) == SQLITE_OK else { throw DatabaseError(database) }
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

    func appendApplicationLifecycle(_ kind: AppLifecycleEventKind, at time: Date, app: ObservedApplication) throws {
        let instance = try processInstance(pid: app.pid, bundle: app.bundleID, name: app.name, launch: app.launchedAt)
        let insert = try prepare("INSERT INTO running_app_observations VALUES(?,?,?,?,?)")
        defer { sqlite3_finalize(insert) }
        try bindEvent(insert, kind: kind.rawValue, at: time)
        guard bindText(insert, 5, instance) == SQLITE_OK else { throw DatabaseError(database) }
        try commitEvent(insert)
        if kind == .disappeared { processes.removeValue(forKey: app.pid) }
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
