import Foundation

typealias Cancel = () -> Void

// One kind of observation: its permission, its availability in capture_channel_events and its initial value.
// The session loop only says when the observation chain starts, breaks and resumes; each channel decides the rest.
protocol CaptureChannel: AnyObject {
    /// Records the initial value, or why it cannot be observed.
    func start() throws
    /// The chain broke (sleep, session switch): stop listening and record the channel as unavailable for `reason`.
    func interrupt(reason: String) throws
    /// The chain continues after an interruption: record a new initial value even if nothing changed.
    func resume() throws
    /// Every second while not interrupted: check permissions and catch missed changes. Unchanged values write nothing.
    func reconcile() throws
    /// Stops listening and writes what is still buffered.
    func stop() throws
}

// Notifications may arrive off the main thread; channels record on the main thread and report failures.
func deliverOnMain(_ failed: @escaping (Error) -> Void, _ record: @escaping () throws -> Void) {
    let work = {
        do { try record() } catch { failed(error) }
    }
    if Thread.isMainThread { work() } else { DispatchQueue.main.async(execute: work) }
}
