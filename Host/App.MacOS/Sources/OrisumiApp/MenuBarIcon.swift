import AppKit
import CoreImage

/// The mascot in the menu bar: full colour while capturing, with an orange dot when something needs
/// attention, faded gray when capture is stopped or the status can't be read. The dev app adds a DEV pill.
enum MenuBarIcon {
    private static let mascot: CGFloat = 18
    private static let badgeHeight: CGFloat = 11
    @MainActor private static var cache: [Health: NSImage] = [:]

    /// Nil when running without an app icon (`swift run`); the caller then shows an SF Symbol.
    @MainActor static func image(for health: Health) -> NSImage? {
        if let image = cache[health] { return image }
        // The bundled mascot, not the app icon: the dev app's icon carries a badge too small to read here.
        guard let icon = Bundle.main.image(forResource: "Mascot") ??
                (Bundle.main.object(forInfoDictionaryKey: "CFBundleIconFile") != nil ? NSImage(named: NSImage.applicationIconName) : nil)
        else { return nil }
        let image = render(icon, health: health, dev: DevBadge.isDevBuild)
        cache[health] = image
        return image
    }

    static func render(_ icon: NSImage, health: Health, dev: Bool = false) -> NSImage {
        let source: NSImage = switch health {
        case .ok, .attention: icon
        case .stopped, .unavailable: grayscale(icon) ?? icon
        }
        let width = mascot + (dev ? 3 + DevBadge.width(height: badgeHeight) : 0)
        let image = NSImage(size: NSSize(width: width, height: mascot), flipped: false) { bounds in
            NSGraphicsContext.current?.imageInterpolation = .high
            let faded = health == .stopped || health == .unavailable
            let icon = NSRect(x: 0, y: 0, width: mascot, height: mascot)
            source.draw(in: icon, from: .zero, operation: .sourceOver, fraction: faded ? 0.55 : 1)
            if dev {
                DevBadge.draw(at: NSPoint(x: mascot + 3, y: (mascot - badgeHeight) / 2), height: badgeHeight,
                              fraction: faded ? 0.55 : 1)
            }
            if health == .attention {
                // A dot with a dark ring reads on both light and dark menu bars.
                let dot = NSRect(x: icon.maxX - 7, y: icon.minY, width: 7, height: 7)
                NSColor.black.withAlphaComponent(0.35).setFill()
                NSBezierPath(ovalIn: dot).fill()
                NSColor.systemOrange.setFill()
                NSBezierPath(ovalIn: dot.insetBy(dx: 1, dy: 1)).fill()
            }
            return true
        }
        image.isTemplate = false
        return image
    }

    private static func grayscale(_ image: NSImage) -> NSImage? {
        guard let tiff = image.tiffRepresentation, let input = CIImage(data: tiff),
              let filter = CIFilter(name: "CIColorControls") else { return nil }
        filter.setValue(input, forKey: kCIInputImageKey)
        filter.setValue(0, forKey: kCIInputSaturationKey)
        guard let output = filter.outputImage else { return nil }
        let representation = NSCIImageRep(ciImage: output)
        let gray = NSImage(size: representation.size)
        gray.addRepresentation(representation)
        return gray
    }
}
