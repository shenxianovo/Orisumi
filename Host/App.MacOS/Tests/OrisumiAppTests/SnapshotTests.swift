import AppKit
import SwiftUI
import XCTest
@testable import OrisumiApp

/// Renders the real window and menu bar icon to PNGs for visual review.
/// Runs only with `ORISUMI_SNAPSHOTS=<directory> swift test --filter SnapshotTests`.
@MainActor
final class SnapshotTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        guard let path = ProcessInfo.processInfo.environment["ORISUMI_SNAPSHOTS"] else {
            throw XCTSkip("Set ORISUMI_SNAPSHOTS to a directory to render snapshots.")
        }
        directory = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSApplication.shared.applicationIconImage = NSImage(contentsOf: packageRoot.appendingPathComponent("Icon/AppIcon.png"))
    }

    func testWindow() throws {
        let attention = try shifted("status")
        var healthy = attention
        healthy["health"] = "ok"
        healthy["collectors"] = (attention["collectors"] as! [[String: Any]]).map { collector in
            var collector = collector
            collector["health"] = "ok"
            collector["problems"] = [String]()
            collector["agent"] = ["state": "running", "pid": 4242, "last_exit_code": NSNull()]
            if var run = collector["run"] as? [String: Any] {
                run["stopped_at_utc_ms"] = NSNull(); run["stop_reason"] = NSNull(); run["error"] = NSNull()
                run["confirmed_until_utc_ms"] = attention["generated_at_utc_ms"]
                collector["run"] = run
            }
            return collector
        }
        let coverage: Coverage = try decode(try shifted("coverage"))

        var stopped = attention
        stopped["health"] = "stopped"
        stopped["collectors"] = (attention["collectors"] as! [[String: Any]]).map { collector in
            var collector = collector
            collector["health"] = "stopped"
            collector["problems"] = ["not_loaded"]
            collector["agent"] = ["state": "not_loaded", "pid": NSNull(), "last_exit_code": NSNull()]
            return collector
        }
        for (name, status) in [("overview-ok", healthy), ("overview-attention", attention), ("overview-stopped", stopped)] {
            let model = AppModel.preview(status: try decode(status), coverage: coverage)
            try render(AppWindow(model: model, startsInMenuBar: false), name: name)
        }
        let model = AppModel.preview(status: try decode(attention), coverage: coverage)
        let window = AppWindow(model: model, startsInMenuBar: false, initialSelection: "macos")
        try render(window, name: "source-macos", size: NSSize(width: 940, height: 1300))
        try render(AppWindow(model: model, startsInMenuBar: false, initialSelection: "browser"), name: "source-browser",
                   size: NSSize(width: 940, height: 1300))
        let error = AppModel.preview(status: nil, coverage: nil,
                                     error: "The CLI exited with code 1: could not open the store.")
        try render(AppWindow(model: error, startsInMenuBar: false), name: "error", appearances: [.aqua])
    }

    /// Renders `status.json` and `coverage.json` saved from a real store, when ORISUMI_SNAPSHOT_DATA names their directory.
    func testRealData() throws {
        guard let path = ProcessInfo.processInfo.environment["ORISUMI_SNAPSHOT_DATA"] else {
            throw XCTSkip("Set ORISUMI_SNAPSHOT_DATA to a directory with status.json and coverage.json.")
        }
        let directory = URL(fileURLWithPath: path, isDirectory: true)
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let status = try decoder.decode(CaptureStatus.self, from: Data(contentsOf: directory.appendingPathComponent("status.json")))
        let coverage = try decoder.decode(Coverage.self, from: Data(contentsOf: directory.appendingPathComponent("coverage.json")))
        let model = AppModel.preview(status: status, coverage: coverage)
        try render(AppWindow(model: model, startsInMenuBar: false), name: "real-overview")
        for collector in status.collectors {
            try render(AppWindow(model: model, startsInMenuBar: false, initialSelection: collector.name),
                       name: "real-\(collector.name)", size: NSSize(width: 940, height: 1300), appearances: [.aqua])
        }
    }

    func testMenuBarIcon() throws {
        let icon = try XCTUnwrap(NSImage(contentsOf: packageRoot.appendingPathComponent("Icon/AppIcon.png")))
        for appearance in [NSAppearance.Name.aqua, .darkAqua] {
            let images = [Health.ok, .attention, .stopped].map { MenuBarIcon.render(icon, health: $0) }
            let strip = NSImage(size: NSSize(width: 44 * images.count + 8, height: 30), flipped: false) { bounds in
                (appearance == .aqua ? NSColor(white: 0.93, alpha: 1) : NSColor(white: 0.16, alpha: 1)).setFill()
                bounds.fill()
                for (index, image) in images.enumerated() {
                    image.draw(in: NSRect(x: 8 + index * 44, y: 6, width: 18, height: 18))
                }
                return true
            }
            try write(strip, scale: 4, name: "menubar-\(appearance == .aqua ? "light" : "dark")")
        }
    }

    // MARK: - Helpers

    private var packageRoot: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    /// The fixture with every `*_utc_ms` moved so its status was generated just now.
    private func shifted(_ name: String) throws -> [String: Any] {
        let status = try JSONSerialization.jsonObject(with: fixtureData("status")) as! [String: Any]
        let offset = Int64(Date.now.timeIntervalSince1970 * 1000) - (status["generated_at_utc_ms"] as! NSNumber).int64Value
        func shift(_ value: Any, key: String?) -> Any {
            if let dictionary = value as? [String: Any] {
                return Dictionary(uniqueKeysWithValues: dictionary.map { ($0.key, shift($0.value, key: $0.key)) })
            }
            if let array = value as? [Any] { return array.map { shift($0, key: key) } }
            if let key, key.hasSuffix("_utc_ms"), let number = value as? NSNumber, !(number is Bool),
               number.int64Value > 0 { return number.int64Value + offset }
            return value
        }
        return shift(try JSONSerialization.jsonObject(with: fixtureData(name)), key: nil) as! [String: Any]
    }

    private func decode<T: Decodable>(_ object: [String: Any]) throws -> T {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: JSONSerialization.data(withJSONObject: object))
    }

    private func fixtureData(_ name: String) throws -> Data {
        try Data(contentsOf: XCTUnwrap(Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures")))
    }

    private func render<V: View>(_ view: V, name: String, size: NSSize = NSSize(width: 940, height: 660),
                                 appearances: [NSAppearance.Name] = [.aqua, .darkAqua]) throws {
        for appearance in appearances {
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                  styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.appearance = NSAppearance(named: appearance)
            window.contentView = NSHostingView(rootView: view.background(Color(nsColor: .windowBackgroundColor)))
            window.setFrame(NSRect(origin: .zero, size: size), display: true)
            window.orderFrontRegardless()
            for _ in 0..<30 { RunLoop.main.run(until: .now.addingTimeInterval(0.05)) }
            let image = try XCTUnwrap(windowImage(window))
            let data = try XCTUnwrap(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
            try data.write(to: directory.appendingPathComponent("\(name)-\(appearance == .aqua ? "light" : "dark").png"))
            window.close()
        }
    }

    /// A process may read its own windows without Screen Recording permission. The function is
    /// obsoleted in the macOS 15 SDK but still exported, so it is looked up at run time.
    private func windowImage(_ window: NSWindow) -> CGImage? {
        typealias Create = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> Unmanaged<CGImage>?
        guard let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "CGWindowListCreateImage") else { return nil }
        let create = unsafeBitCast(symbol, to: Create.self)
        let options = CGWindowImageOption([.boundsIgnoreFraming, .bestResolution])
        return create(.null, CGWindowListOption.optionIncludingWindow.rawValue, UInt32(window.windowNumber),
                      options.rawValue)?.takeRetainedValue()
    }

    private func write(_ image: NSImage, scale: CGFloat, name: String) throws {
        let size = NSSize(width: image.size.width * scale, height: image.size.height * scale)
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        NSGraphicsContext.current?.imageInterpolation = .none
        image.draw(in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: directory.appendingPathComponent("\(name).png"))
    }
}
