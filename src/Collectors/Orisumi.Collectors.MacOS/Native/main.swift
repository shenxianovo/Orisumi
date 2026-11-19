import Darwin
import Foundation

func usage() -> Never {
    fputs("Usage: orisumi-macos-foreground DATABASE --host=JSON [SECONDS] [--windows] [--afk-threshold=SECONDS] [--input] [--microphone] [--audio-output] [--camera]\n", stderr)
    exit(2)
}

var arguments = CommandLine.arguments.dropFirst()
let hostOption = "--host="
guard let hostIndex = arguments.firstIndex(where: { $0.hasPrefix(hostOption) }),
      let host = try? JSONDecoder().decode(HostObservation.self, from: Data(arguments[hostIndex].dropFirst(hostOption.count).utf8))
else { usage() }
arguments.remove(at: hostIndex)
var mediaChannels: Set<MediaChannel> = []
while arguments.count > 1, let option = arguments.last,
      let channel = MediaChannel.allCases.first(where: { "--" + $0.rawValue.replacingOccurrences(of: "_", with: "-") == option }) {
    mediaChannels.insert(channel)
    arguments.removeLast()
}
let captureInput = arguments.count > 1 && arguments.last == "--input"
if captureInput { arguments.removeLast() }
var afkThresholdMS: Int64 = 300_000
let thresholdOption = "--afk-threshold="
if let option = arguments.last, option.hasPrefix(thresholdOption) {
    guard let seconds = Int32(option.dropFirst(thresholdOption.count)), seconds > 0 else { usage() }
    afkThresholdMS = Int64(seconds) * 1000
    arguments.removeLast()
}
let captureWindows = arguments.count > 1 && arguments.last == "--windows"
if captureWindows { arguments.removeLast() }
var seconds: Int?
if arguments.count == 2, let duration = Int32(arguments.last!), duration > 0 {
    seconds = Int(duration)
}
guard let path = arguments.first,
      arguments.count == 1 || seconds != nil
else {
    usage()
}

// The .NET parent handles terminal Ctrl+C and closes the pipe to request a clean stop.
signal(SIGINT, SIG_IGN)
umask(0o077)
do {
    try captureForeground(in: ForegroundDatabase(path: path, host: host, captureWindows: captureWindows, captureInput: captureInput,
                                                 mediaChannels: mediaChannels),
                          seconds: seconds, captureWindows: captureWindows, afkThresholdMS: afkThresholdMS,
                          captureInput: captureInput, mediaChannels: mediaChannels)
} catch {
    fputs("orisumi: macOS foreground capture failed: \(error.localizedDescription)\n", stderr)
    exit(1)
}
