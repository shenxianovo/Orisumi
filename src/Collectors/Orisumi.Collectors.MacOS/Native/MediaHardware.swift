import AppKit
import CoreAudio
import CoreMediaIO

enum MediaHardware {
    private enum System {
        case audio, camera

        func size(_ id: UInt32, _ selector: UInt32) -> UInt32? {
            var size: UInt32 = 0
            let status: OSStatus
            switch self {
            case .audio:
                var address = audioAddress(selector)
                status = AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size)
            case .camera:
                var address = cameraAddress(selector)
                status = CMIOObjectGetPropertyDataSize(id, &address, 0, nil, &size)
            }
            return status == noErr ? size : nil
        }

        func read(_ id: UInt32, _ selector: UInt32, size: UInt32, into buffer: UnsafeMutableRawPointer) -> UInt32? {
            var used = size
            let status: OSStatus
            switch self {
            case .audio:
                var address = audioAddress(selector)
                status = AudioObjectGetPropertyData(id, &address, 0, nil, &used, buffer)
            case .camera:
                var address = cameraAddress(selector)
                status = CMIOObjectGetPropertyData(id, &address, 0, nil, size, &used, buffer)
            }
            return status == noErr ? used : nil
        }

        func objects(_ id: UInt32, _ selector: UInt32) -> [UInt32]? {
            guard let size = size(id, selector) else { return nil }
            guard size > 0 else { return [] }
            let stride = UInt32(MemoryLayout<UInt32>.stride)
            var ids = [UInt32](repeating: 0, count: Int(size / stride))
            guard let used = ids.withUnsafeMutableBytes({ read(id, selector, size: size, into: $0.baseAddress!) })
            else { return nil }
            return Array(ids.prefix(Int(used / stride)))
        }

        func integer(_ id: UInt32, _ selector: UInt32) -> UInt32? {
            var value: UInt32 = 0
            let size = UInt32(MemoryLayout.size(ofValue: value))
            return read(id, selector, size: size, into: &value) == size ? value : nil
        }

        func string(_ id: UInt32, _ selector: UInt32) -> String? {
            var value: Unmanaged<CFString>?
            let size = UInt32(MemoryLayout.size(ofValue: value))
            guard read(id, selector, size: size, into: &value) == size else { return nil }
            return value.map { $0.takeRetainedValue() as String }
        }

        // Both APIs enqueue these property callbacks on the main queue. SQLite and the
        // observer state stay on the same thread as the initial snapshot and session events.
        func listen(_ id: UInt32, _ selector: UInt32, changed: @escaping () -> Void) -> MediaObjectSource.Cancel? {
            switch self {
            case .audio:
                var address = audioAddress(selector)
                let listener: AudioObjectPropertyListenerBlock = { _, _ in changed() }
                guard AudioObjectAddPropertyListenerBlock(id, &address, .main, listener) == noErr else { return nil }
                return {
                    var address = audioAddress(selector)
                    AudioObjectRemovePropertyListenerBlock(id, &address, .main, listener)
                }
            case .camera:
                var address = cameraAddress(selector)
                let listener: CMIOObjectPropertyListenerBlock = { _, _ in changed() }
                guard CMIOObjectAddPropertyListenerBlock(id, &address, .main, listener) == noErr else { return nil }
                return {
                    var address = cameraAddress(selector)
                    CMIOObjectRemovePropertyListenerBlock(id, &address, .main, listener)
                }
            }
        }
    }

    static func audio(applications: ApplicationLifecycleObserver) -> MediaObjectSource {
        let hardware = System.audio
        let system = AudioObjectID(kAudioObjectSystemObject)
        func selector(_ channel: MediaChannel) -> UInt32 {
            channel == .microphone ? kAudioProcessPropertyIsRunningInput : kAudioProcessPropertyIsRunningOutput
        }
        func matchApplication(_ id: UInt32, _ pid: pid_t, _ bundle: String?) -> String? {
            guard let app = NSRunningApplication(processIdentifier: pid), !app.isTerminated,
                  bundle == nil || bundle == "" || bundle == app.bundleIdentifier,
                  let instance = applications.observedInstance(of: app)
            else { return nil }
            // The live PID must still map back to this HAL process object.
            var qualifier = pid
            var address = audioAddress(kAudioHardwarePropertyTranslatePIDToProcessObject)
            var object = AudioObjectID(kAudioObjectUnknown)
            var size = UInt32(MemoryLayout.size(ofValue: object))
            let status = AudioObjectGetPropertyData(system, &address, UInt32(MemoryLayout.size(ofValue: qualifier)),
                                                    &qualifier, &size, &object)
            return status == noErr && object == id && !app.isTerminated ? instance : nil
        }
        return MediaObjectSource(
            list: { hardware.objects(system, kAudioHardwarePropertyProcessObjectList) },
            watchList: { hardware.listen(system, kAudioHardwarePropertyProcessObjectList, changed: $0) },
            watchRestart: { hardware.listen(system, kAudioHardwarePropertyServiceRestarted, changed: $0) },
            identity: { id in
                let pid = hardware.integer(id, kAudioProcessPropertyPID).map { pid_t(bitPattern: $0) }
                let bundle = hardware.string(id, kAudioProcessPropertyBundleID)
                let instance = pid.flatMap { $0 > 0 ? matchApplication(id, $0, bundle) : nil }
                return .audio(pid: pid, bundleID: bundle, processInstanceID: instance)
            },
            matchApplication: matchApplication,
            read: { id, channel in hardware.integer(id, selector(channel)).map { $0 != 0 } },
            watch: { id, channel, changed in hardware.listen(id, selector(channel), changed: changed) }
        )
    }

    static func camera() -> MediaObjectSource {
        let hardware = System.camera
        let system = CMIOObjectID(kCMIOObjectSystemObject)
        let devices = UInt32(kCMIOHardwarePropertyDevices)
        let running = UInt32(kCMIODevicePropertyDeviceIsRunningSomewhere)
        return MediaObjectSource(
            list: { hardware.objects(system, devices) },
            watchList: { hardware.listen(system, devices, changed: $0) },
            watchRestart: nil,
            identity: { id in
                .camera(uid: hardware.string(id, UInt32(kCMIODevicePropertyDeviceUID)),
                        name: hardware.string(id, UInt32(kCMIOObjectPropertyName)))
            },
            matchApplication: nil,
            read: { id, _ in hardware.integer(id, running).map { $0 != 0 } },
            watch: { id, _, changed in hardware.listen(id, running, changed: changed) }
        )
    }

    private static func audioAddress(_ selector: UInt32) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    private static func cameraAddress(_ selector: UInt32) -> CMIOObjectPropertyAddress {
        CMIOObjectPropertyAddress(mSelector: selector, mScope: UInt32(kCMIOObjectPropertyScopeGlobal),
                                  mElement: UInt32(kCMIOObjectPropertyElementMain))
    }
}
