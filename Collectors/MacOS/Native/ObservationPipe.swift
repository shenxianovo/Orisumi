import Darwin
import Foundation

// Block only the pipe worker when the reader is slow. Overflow ends the run with a visible
// failure. The independent stdin watcher can still terminate the helper if the parent dies.
final class ObservationPipe {
    static let capacity = 4 * 1024 * 1024

    enum Failure: LocalizedError {
        case backpressure
        var errorDescription: String? { "macOS observation pipe backlog exceeded its limit" }
    }

    private let queue = DispatchQueue(label: "orisumi.macos.stdout")
    private let lock = NSLock()
    private var pendingBytes = 0

    func send(_ data: Data) throws {
        try lock.withLock {
            guard pendingBytes + data.count <= Self.capacity else { throw Failure.backpressure }
            pendingBytes += data.count
        }
        queue.async {
            do { try FileHandle.standardOutput.write(contentsOf: data) }
            catch {
                fputs("orisumi: macOS observation pipe write failed: \(error.localizedDescription)\n", stderr)
                _exit(1)
            }
            self.lock.withLock { self.pendingBytes -= data.count }
        }
    }

    func drain() { queue.sync {} }

    func drain(until deadline: DispatchTime) {
        let drained = DispatchSemaphore(value: 0)
        queue.async { drained.signal() }
        _ = drained.wait(timeout: deadline)
    }
}
