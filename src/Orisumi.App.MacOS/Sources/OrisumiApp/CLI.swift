import Foundation

struct CLIConfiguration: Equatable, Sendable {
    let executable: URL
    let store: URL?

    private static func installedStore(for application: URL) -> URL? {
        let host = application.deletingLastPathComponent()
        let app = host.deletingLastPathComponent()
        guard application.lastPathComponent == "Orisumi.app",
              host.lastPathComponent == "host", app.lastPathComponent == "app" else { return nil }
        return app.deletingLastPathComponent()
    }

    init(arguments: [String], bundle: URL) throws {
        var options: [String: URL] = [:]
        var arguments = arguments[...]
        while let option = arguments.popFirst() {
            guard option == "--cli" || option == "--store", options[option] == nil,
                  let path = arguments.popFirst(), !path.isEmpty, !path.hasPrefix("--") else {
                throw CLIError.message("Usage: OrisumiApp --cli <path-to-orisumi> [--store <directory>]")
            }
            options[option] = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
        }

        let application = bundle.resolvingSymlinksInPath()
        let bundledCLI = application.pathExtension == "app" ? application.appendingPathComponent("Contents/MacOS/orisumi") : nil
        guard let cli = options["--cli"] ?? bundledCLI else {
            throw CLIError.message("Pass --cli <path-to-orisumi> when running from a checkout.")
        }
        self.executable = cli
        self.store = options["--store"] ?? Self.installedStore(for: application)
    }
}

enum CLIError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self { case .message(let message): return message }
    }
}

struct CLI: Sendable {
    let configuration: CLIConfiguration

    func status() async throws -> CaptureStatus {
        try await decode(CaptureStatus.self, arguments: ["status", "--json"])
    }

    func coverage(from: Date, to: Date) async throws -> Coverage {
        try await decode(Coverage.self, arguments: [
            "coverage", "--from", from.ISO8601Format(), "--to", to.ISO8601Format(.init(includingFractionalSeconds: true)),
        ])
    }

    func capture(_ action: CaptureAction) async throws {
        _ = try await run([action.rawValue])
    }

    private func decode<Value: Decodable>(_ type: Value.Type, arguments: [String]) async throws -> Value {
        let data = try await run(arguments)
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        do {
            return try decoder.decode(type, from: data)
        } catch {
            throw CLIError.message("Invalid JSON from capture \(arguments[0]): \(String(describing: error))")
        }
    }

    private func run(_ capture: [String]) async throws -> Data {
        let executable = configuration.executable
        let store = configuration.store.map { ["--store", $0.path] } ?? []
        let arguments = store + ["capture"] + capture
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(with: Result { try Self.call(executable, arguments: arguments) })
            }
        }
    }

    private static func call(_ executable: URL, arguments: [String]) throws -> Data {
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw CLIError.message("The CLI is missing or is not executable.")
        }

        // Files capture both streams without filling a pipe or waiting for a descendant
        // that inherited a descriptor to exit. Keep the output private and short-lived.
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("orisumi-app-\(UUID())")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: temporary) }
        let stdout = temporary.appendingPathComponent("stdout")
        let stderr = temporary.appendingPathComponent("stderr")
        try Data().write(to: stdout)
        try Data().write(to: stderr)
        let output = try FileHandle(forWritingTo: stdout)
        let errors = try FileHandle(forWritingTo: stderr)
        defer {
            try? output.close()
            try? errors.close()
        }

        let exited = DispatchSemaphore(value: 0)
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output
        process.standardError = errors
        process.terminationHandler = { _ in exited.signal() }
        try process.run()
        let timedOut = exited.wait(timeout: .now() + 15) == .timedOut
        if timedOut && process.isRunning {
            let pid = process.processIdentifier
            kill(getpgid(pid) == pid ? -pid : pid, SIGKILL)
        }
        process.waitUntilExit()

        if timedOut || process.terminationStatus != 0 {
            let error = (try? String(contentsOf: stderr, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
            let cause = timedOut ? "The CLI timed out after 15 seconds." : "The CLI exited with code \(process.terminationStatus)."
            throw CLIError.message([cause, error].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n"))
        }
        return try Data(contentsOf: stdout)
    }
}

enum CaptureAction: String {
    case start, stop
}
