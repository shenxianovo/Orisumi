import Foundation
import SQLite3

enum DatabaseSchema {
    private struct SchemaError: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    static func ensureCreated(_ database: OpaquePointer) throws {
        let directory = Bundle.main.executableURL!.deletingLastPathComponent()
            .appendingPathComponent("Orisumi.Collectors.MacOS/Schema", isDirectory: true)
        let versionText = try String(contentsOf: directory.appendingPathComponent("Version.txt"), encoding: .utf8)
        guard let version = Int64(versionText.trimmingCharacters(in: .whitespacesAndNewlines)), version > 0 else {
            throw SchemaError("Invalid macOS schema version")
        }
        func execute(_ sql: String) throws {
            guard sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK else {
                throw SchemaError(String(cString: sqlite3_errmsg(database)))
            }
        }
        func scalar(_ sql: String) throws -> Int64 {
            var statement: OpaquePointer?
            defer { sqlite3_finalize(statement) }
            guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
                  sqlite3_step(statement) == SQLITE_ROW else {
                throw SchemaError(String(cString: sqlite3_errmsg(database)))
            }
            return sqlite3_column_int64(statement, 0)
        }

        try execute("BEGIN IMMEDIATE")
        var committed = false
        defer { if !committed { sqlite3_exec(database, "ROLLBACK", nil, nil, nil) } }
        let actual = try scalar("PRAGMA user_version")
        if actual != version {
            guard actual == 0,
                  try scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'") == 0 else {
                throw SchemaError("Unsupported macOS database schema. Expected version \(version). Stop this collector and rebuild its output database.")
            }
            for group in ["Tables", "Indexes", "Views"] {
                let files = try FileManager.default.contentsOfDirectory(
                    at: directory.appendingPathComponent(group, isDirectory: true),
                    includingPropertiesForKeys: nil)
                    .filter { $0.pathExtension == "sql" }
                    .sorted { $0.lastPathComponent < $1.lastPathComponent }
                for file in files {
                    do { try execute(String(contentsOf: file, encoding: .utf8)) }
                    catch { throw SchemaError("Could not initialize \(group)/\(file.lastPathComponent): \(error.localizedDescription)") }
                }
            }
            try execute("PRAGMA user_version = \(version)")
        }
        try execute("COMMIT")
        committed = true
    }
}
