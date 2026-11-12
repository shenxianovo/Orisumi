import Carbon
import CoreGraphics
import Foundation
import IOKit.hidsystem

struct KeyboardPress {
    let keyCode: Int64
    let modifiersAtPress: Int64
    let releasedAt: Date?
}

struct KeyboardPressAccumulator {
    private struct PendingPress {
        let runSequence: Int64
        let pressedAt: Date
        let modifiersAtPress: Int64

        func finish(keyCode: Int64, releasedAt: Date?) -> InputObservation {
            InputObservation(runSequence: runSequence, observedAt: pressedAt,
                             event: .keyboardPress(KeyboardPress(keyCode: keyCode, modifiersAtPress: modifiersAtPress,
                                                                 releasedAt: releasedAt)))
        }
    }

    private static let modifierKeyMasks: [Int64: UInt64] = [
        Int64(kVK_Shift): UInt64(NX_DEVICELSHIFTKEYMASK),
        Int64(kVK_RightShift): UInt64(NX_DEVICERSHIFTKEYMASK),
        Int64(kVK_Command): UInt64(NX_DEVICELCMDKEYMASK),
        Int64(kVK_RightCommand): UInt64(NX_DEVICERCMDKEYMASK),
        Int64(kVK_Option): UInt64(NX_DEVICELALTKEYMASK),
        Int64(kVK_RightOption): UInt64(NX_DEVICERALTKEYMASK),
        Int64(kVK_Control): UInt64(NX_DEVICELCTLKEYMASK),
        Int64(kVK_RightControl): UInt64(NX_DEVICERCTLKEYMASK),
        Int64(kVK_Function): CGEventFlags.maskSecondaryFn.rawValue,
    ]
    private static let shortcutModifiers: CGEventFlags = [.maskShift, .maskControl, .maskAlternate, .maskCommand]
    private var held: [Int64: PendingPress] = [:]

    mutating func consume(_ type: CGEventType, keyCode: Int64, flags: CGEventFlags, isRepeat: Bool,
                          at time: Date, reserveSequence: () -> Int64) -> InputObservation? {
        guard keyCode >= 0 else { return nil }
        let isDown: Bool
        switch type {
        case .keyDown:
            guard !isRepeat else { return nil }
            isDown = true
        case .keyUp:
            isDown = false
        case .flagsChanged:
            guard let mask = Self.modifierKeyMasks[keyCode] else { return nil }
            isDown = flags.rawValue & mask != 0
            if isDown && held[keyCode] != nil { return nil }
        default:
            return nil
        }

        guard isDown else {
            return held.removeValue(forKey: keyCode)?.finish(keyCode: keyCode, releasedAt: time)
        }
        let press = PendingPress(runSequence: reserveSequence(), pressedAt: time,
                                 modifiersAtPress: Int64(flags.intersection(Self.shortcutModifiers).rawValue))
        return held.updateValue(press, forKey: keyCode)?.finish(keyCode: keyCode, releasedAt: nil)
    }

    mutating func interrupt() -> [InputObservation] {
        let incomplete = held.map { $0.value.finish(keyCode: $0.key, releasedAt: nil) }
        held.removeAll(keepingCapacity: true)
        return incomplete
    }
}
