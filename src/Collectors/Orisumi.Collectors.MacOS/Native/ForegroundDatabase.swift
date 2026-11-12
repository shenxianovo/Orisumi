import Foundation
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
    private var windowInsert: OpaquePointer?
    private var appLifecycleInsert: OpaquePointer?
    private var keyboardInsert: OpaquePointer?
    private var mouseInsert: OpaquePointer?
    private let runID = UUID().uuidString.lowercased()
    private var sequence: Int64 = 0

    init(path: String, captureWindows: Bool, captureInput: Bool) throws {
        var connection: OpaquePointer?
        guard sqlite3_open(path, &connection) == SQLITE_OK, let connection else {
            let error = DatabaseError(connection)
            sqlite3_close(connection)
            throw error
        }

        let schema = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS capture_runs (
                run_id TEXT NOT NULL PRIMARY KEY,
                started_at_utc_ms INTEGER NOT NULL,
                stopped_at_utc_ms INTEGER,
                afk_threshold_ms INTEGER CHECK (afk_threshold_ms > 0)
            );
            CREATE TABLE IF NOT EXISTS time_zone_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                time_zone_id TEXT NOT NULL,
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE INDEX IF NOT EXISTS time_zone_events_by_run_and_time
                ON time_zone_events (run_id, observed_at_utc_ms, run_sequence);
            CREATE TABLE IF NOT EXISTS afk_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'transition')),
                state TEXT NOT NULL CHECK (state IN ('active', 'idle')),
                idle_ms INTEGER NOT NULL CHECK (idle_ms >= 0),
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE TABLE IF NOT EXISTS foreground_app_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('activated', 'snapshot')),
                bundle_id TEXT,
                app_name TEXT,
                pid INTEGER,
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE TABLE IF NOT EXISTS app_lifecycle_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'appeared', 'disappeared')),
                pid INTEGER NOT NULL,
                bundle_id TEXT,
                app_name TEXT,
                app_launched_at_utc_ms INTEGER,
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE TABLE IF NOT EXISTS system_sleep_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('sleep', 'wake')),
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE TABLE IF NOT EXISTS user_session_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('session_active', 'session_inactive')),
                PRIMARY KEY (run_id, run_sequence)
            );
            """
        let windowSchema = """
            CREATE TABLE IF NOT EXISTS focused_window_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('snapshot', 'focus_changed', 'title_changed')),
                pid INTEGER NOT NULL,
                window_title TEXT,
                PRIMARY KEY (run_id, run_sequence)
            );
            """
        let inputSchema = """
            CREATE TABLE IF NOT EXISTS keyboard_presses (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                key_code INTEGER NOT NULL CHECK (key_code >= 0),
                pressed_at_utc_ms INTEGER NOT NULL,
                released_at_utc_ms INTEGER,
                modifiers_at_press INTEGER NOT NULL,
                PRIMARY KEY (run_id, run_sequence)
            );
            CREATE TABLE IF NOT EXISTS mouse_button_events (
                run_id TEXT NOT NULL REFERENCES capture_runs(run_id),
                run_sequence INTEGER NOT NULL,
                observed_at_utc_ms INTEGER NOT NULL,
                event_kind TEXT NOT NULL CHECK (event_kind IN ('button_down', 'button_up')),
                x_points REAL NOT NULL,
                y_points REAL NOT NULL,
                button INTEGER NOT NULL CHECK (button >= 0),
                PRIMARY KEY (run_id, run_sequence)
            );
            """
        guard sqlite3_busy_timeout(connection, 3000) == SQLITE_OK,
              sqlite3_exec(connection, schema, nil, nil, nil) == SQLITE_OK,
              !captureWindows || sqlite3_exec(connection, windowSchema, nil, nil, nil) == SQLITE_OK,
              !captureInput || sqlite3_exec(connection, inputSchema, nil, nil, nil) == SQLITE_OK
        else {
            let error = DatabaseError(connection)
            sqlite3_close(connection)
            throw error
        }
        database = connection
    }

    deinit {
        for statement in inserts.values { sqlite3_finalize(statement) }
        sqlite3_finalize(afkInsert)
        sqlite3_finalize(windowInsert)
        sqlite3_finalize(appLifecycleInsert)
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
                INSERT INTO \(table.rawValue) (\(columns), bundle_id, app_name, pid)
                VALUES (?, ?, ?, ?, ?, ?, ?)
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
            INSERT INTO capture_runs (run_id, started_at_utc_ms, afk_threshold_ms) VALUES (?1, ?2, ?3)
            """, at: time, afkThresholdMS: afkThresholdMS)
    }

    func stop(at time: Date) throws {
        try writeRunTime("UPDATE capture_runs SET stopped_at_utc_ms = ?2 WHERE run_id = ?1", at: time)
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
            let pid = app?.pid
            guard bindText(insert, 5, app?.bundleID) == SQLITE_OK,
                  bindText(insert, 6, app?.name) == SQLITE_OK,
                  (pid.map { sqlite3_bind_int(insert, 7, $0) } ?? sqlite3_bind_null(insert, 7)) == SQLITE_OK
            else {
                throw DatabaseError(database)
            }
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

    func appendWindow(_ kind: WindowEventKind, at time: Date, pid: pid_t, title: String?) throws {
        if windowInsert == nil {
            windowInsert = try prepare("""
                INSERT INTO focused_window_events
                    (run_id, run_sequence, observed_at_utc_ms, event_kind, pid, window_title)
                VALUES (?, ?, ?, ?, ?, ?)
                """)
        }
        let insert = windowInsert!
        defer { sqlite3_reset(insert) }
        try bindEvent(insert, kind: kind.rawValue, at: time)
        guard sqlite3_bind_int(insert, 5, pid) == SQLITE_OK,
              bindText(insert, 6, title) == SQLITE_OK
        else {
            throw DatabaseError(database)
        }
        try commitEvent(insert)
    }

    func appendApplicationLifecycle(_ kind: AppLifecycleEventKind, at time: Date, app: ObservedApplication) throws {
        let insert: OpaquePointer
        if let appLifecycleInsert {
            insert = appLifecycleInsert
        } else {
            insert = try prepare("""
                INSERT INTO app_lifecycle_events
                    (run_id, run_sequence, observed_at_utc_ms, event_kind, pid, bundle_id, app_name, app_launched_at_utc_ms)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """)
            appLifecycleInsert = insert
        }
        defer { sqlite3_reset(insert) }
        try bindEvent(insert, kind: kind.rawValue, at: time)
        let launchTime = kind == .disappeared ? nil : app.launchedAt
        let launchResult = launchTime.map { sqlite3_bind_int64(insert, 8, timestamp($0)) }
            ?? sqlite3_bind_null(insert, 8)
        guard sqlite3_bind_int(insert, 5, app.pid) == SQLITE_OK,
              bindText(insert, 6, app.bundleID) == SQLITE_OK,
              bindText(insert, 7, app.name) == SQLITE_OK,
              launchResult == SQLITE_OK
        else {
            throw DatabaseError(database)
        }
        try commitEvent(insert)
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
