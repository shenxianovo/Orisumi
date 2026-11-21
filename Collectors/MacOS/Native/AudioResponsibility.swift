import Darwin

enum AudioResponsibility {
    typealias Lookup = @convention(c) (pid_t) -> pid_t

    private static let lookup: Lookup? = {
        // RTLD_DEFAULT searches the loaded images, including libquarantine on macOS.
        let defaultHandle = UnsafeMutableRawPointer(bitPattern: -2)
        guard let symbol = dlsym(defaultHandle, "responsibility_get_pid_responsible_for_pid") else { return nil }
        return unsafeBitCast(symbol, to: Lookup.self)
    }()

    static func responsiblePID(_ pid: pid_t) -> pid_t {
        resolve(pid, using: lookup)
    }

    static func resolve(_ pid: pid_t, using lookup: ((pid_t) -> pid_t)?) -> pid_t {
        guard let responsible = lookup?(pid), responsible > 0 else { return pid }
        return responsible
    }

    static func application(for pid: pid_t, bundle: String?, responsiblePID: (pid_t) -> pid_t,
                            observedInstance: (pid_t, String?) -> String?) -> String? {
        let responsible = responsiblePID(pid)
        return observedInstance(responsible, responsible == pid ? bundle : nil)
    }
}
