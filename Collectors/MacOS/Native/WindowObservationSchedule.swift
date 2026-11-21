import Foundation

struct WindowObservationSchedule {
    var focusNotification = false
    var titleNotification = false
    var hasWindow = false
    var readSucceeded = false
    var lastRead: TimeInterval?

    func shouldRead(at uptime: TimeInterval) -> Bool {
        guard readSucceeded, focusNotification, !hasWindow || titleNotification, let lastRead else { return true }
        // Notifications remain the main path; a sparse reconciliation detects missed updates.
        return uptime - lastRead >= 60
    }
}
