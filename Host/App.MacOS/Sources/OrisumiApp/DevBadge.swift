import AppKit

/// The dev instance's app (bundle ID `dev.orisumi.canary`) wears a DEV badge in the menu bar and on its icon,
/// so it is never mistaken for production.
enum DevBadge {
    static let bundleIdentifier = "dev.orisumi.canary"
    static var isDevBuild: Bool { Bundle.main.bundleIdentifier == bundleIdentifier }

    /// Width of the pill drawn at `height`, sized to its text.
    static func width(height: CGFloat) -> CGFloat { text(height: height).size().width + height * 0.8 }

    /// White heavy capitals on indigo read on light and dark backgrounds alike.
    static func draw(at origin: NSPoint, height: CGFloat, fraction: CGFloat = 1) {
        let rect = NSRect(x: origin.x, y: origin.y, width: width(height: height), height: height)
        NSColor.systemIndigo.withAlphaComponent(fraction).setFill()
        NSBezierPath(roundedRect: rect, xRadius: height * 0.3, yRadius: height * 0.3).fill()
        let label = text(height: height, fraction: fraction)
        let font = label.attribute(.font, at: 0, effectiveRange: nil) as! NSFont
        // Centre the capitals: the baseline sits half a cap height below the middle.
        let baseline = rect.midY - font.capHeight / 2
        label.draw(at: NSPoint(x: rect.midX - label.size().width / 2, y: baseline + font.descender))
    }

    /// The app icon with the badge across its lower edge, for Finder, the Dock and System Settings.
    static func icon(from source: NSImage, pixels: Int) -> NSBitmapImageRep {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                                      samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        let size = CGFloat(pixels)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        NSGraphicsContext.current?.imageInterpolation = .high
        source.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
        let height = size * 0.2
        draw(at: NSPoint(x: (size - width(height: height)) / 2, y: size * 0.07), height: height)
        NSGraphicsContext.restoreGraphicsState()
        return bitmap
    }

    /// `OrisumiApp --badge-icon <source.png> <badged.png>`: the build calls this for the dev app's icon.
    static func writeIcon(from source: String, to destination: String) -> Int32 {
        guard let image = NSImage(contentsOfFile: source),
              let png = icon(from: image, pixels: 1024).representation(using: .png, properties: [:]) else {
            FileHandle.standardError.write("Can't read the icon \(source)\n".data(using: .utf8)!)
            return 1
        }
        do { try png.write(to: URL(fileURLWithPath: destination)) } catch {
            FileHandle.standardError.write("Can't write \(destination): \(error.localizedDescription)\n".data(using: .utf8)!)
            return 1
        }
        return 0
    }

    private static func text(height: CGFloat, fraction: CGFloat = 1) -> NSAttributedString {
        NSAttributedString(string: "DEV", attributes: [
            .font: NSFont.systemFont(ofSize: height * 0.68, weight: .heavy),
            .foregroundColor: NSColor.white.withAlphaComponent(fraction),
            .kern: height * 0.04,
        ])
    }
}
