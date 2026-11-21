import Darwin
import Foundation

func usage() -> Never {
    fputs("Usage: orisumi-macos-foreground [SECONDS] [--windows] [--afk-threshold=SECONDS] [--input] [--microphone] [--audio-output] [--camera]\n", stderr)
    exit(2)
}

var arguments = CommandLine.arguments.dropFirst()
var mediaChannels: Set<MediaChannel> = []
while let option = arguments.last,
      let channel = MediaChannel.allCases.first(where: { "--" + $0.rawValue.replacingOccurrences(of: "_", with: "-") == option }) {
    mediaChannels.insert(channel)
    arguments.removeLast()
}
let captureInput = arguments.last == "--input"
if captureInput { arguments.removeLast() }
var afkThresholdMS: Int64 = 300_000
let thresholdOption = "--afk-threshold="
if let option = arguments.last, option.hasPrefix(thresholdOption) {
    guard let seconds = Int32(option.dropFirst(thresholdOption.count)), seconds > 0 else { usage() }
    afkThresholdMS = Int64(seconds) * 1000
    arguments.removeLast()
}
let captureWindows = arguments.last == "--windows"
if captureWindows { arguments.removeLast() }
var seconds: Int?
if arguments.count == 1, let duration = Int32(arguments.last!), duration > 0 {
    seconds = Int(duration)
}
guard arguments.isEmpty || seconds != nil else { usage() }

// The .NET parent handles terminal Ctrl+C and sends an explicit stop command.
signal(SIGINT, SIG_IGN)
signal(SIGPIPE, SIG_IGN)
let pipe = ObservationPipe()
do {
    try captureForeground(in: ObservationEvents(send: pipe.send), seconds: seconds,
                          captureWindows: captureWindows, afkThresholdMS: afkThresholdMS,
                          captureInput: captureInput, mediaChannels: mediaChannels)
    pipe.drain()
} catch {
    fputs("orisumi: macOS foreground capture failed: \(error.localizedDescription)\n", stderr)
    pipe.drain(until: .now() + .seconds(2))
    exit(1)
}
