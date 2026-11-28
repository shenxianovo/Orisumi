using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orisumi.App;

/// The mascot in the notification area, as on the macOS menu bar: full colour while capturing, with an orange dot
/// when something needs attention, faded gray when capture is stopped or the status can't be read. A non-production
/// instance adds an indigo "D" tag in the upper left, the colour of the macOS DEV pill, diagonal to the dot.
internal sealed unsafe class TrayIcons : IDisposable
{
    // Larger than any notification-area icon (64px at 400%), small enough to keep.
    private const int MasterSize = 256;
    private readonly string source;
    private readonly Dictionary<(Health, int), nint> cache = [];
    private byte[]? master;

    internal TrayIcons(string source, bool tagged)
    {
        this.source = source;
        Tagged = tagged;
        var input = new Native.GdiplusStartupInput { GdiplusVersion = 1 };
        Check(Native.GdiplusStartup(out _, &input, 0), "GdiplusStartup");
    }

    internal bool Tagged { get; }

    internal nint Get(Health health, int size)
    {
        if (cache.TryGetValue((health, size), out var icon)) return icon;
        icon = Create(Render(health, size), size);
        cache[(health, size)] = icon;
        return icon;
    }

    /// Straight-alpha BGRA, top row first.
    internal byte[] Render(Health health, int size)
    {
        var pixels = Scale(size);
        var faded = health is Health.Stopped or Health.Unavailable;
        if (faded)
        {
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var gray = (byte)Math.Round(0.114 * pixels[index] + 0.587 * pixels[index + 1] + 0.299 * pixels[index + 2]);
                pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
                pixels[index + 3] = (byte)Math.Round(pixels[index + 3] * 0.55);
            }
        }
        var unit = size / 16.0;
        if (Tagged)
        {
            // systemIndigo with a dark rim and a white D; it stays legible when faded, so a stopped dev and prod still differ.
            var opacity = faded ? 0.85 : 1;
            double U(double units) => units * unit;
            Paint(pixels, size, (x, y) => RoundedRect(x, y, 0, 0, U(8), U(8), U(2)), 0, 0, 0, 0.5 * opacity);
            Paint(pixels, size, (x, y) => RoundedRect(x, y, U(1), U(1), U(6), U(6), U(1.5)), 88, 86, 214, opacity);
            Paint(pixels, size, (x, y) => RoundedRight(x, y, U(3), U(2), U(3.5), U(4)) &&
                !RoundedRight(x, y, U(4.25), U(3.25), U(1.25), U(1.5)), 255, 255, 255, opacity);
        }
        if (health == Health.Attention)
        {
            // A dot with a dark ring reads on light and dark taskbars alike.
            var radius = 3.5 * unit;
            var (centerX, centerY) = (size - radius, size - radius);
            Paint(pixels, size, (x, y) => Circle(x, y, centerX, centerY, radius), 0, 0, 0, 0.45);
            Paint(pixels, size, (x, y) => Circle(x, y, centerX, centerY, radius - unit), 255, 149, 0, 1);
        }
        return pixels;
    }

    private byte[] Scale(int size)
    {
        if (master is null)
        {
            // GDI+ keeps a file it loaded open, and an open file stops an update from moving this program's
            // directory away; so the file is read once into memory and closed.
            Check(Native.GdipCreateBitmapFromFile(source, out var file), $"Load {source}");
            try { master = Draw(file, MasterSize); }
            finally { Native.GdipDisposeImage(file); }
        }
        fixed (byte* scan0 = master)
        {
            Check(Native.GdipCreateBitmapFromScan0(MasterSize, MasterSize, MasterSize * 4, Argb, scan0, out var image), "Bitmap");
            try { return Draw(image, size); }
            finally { Native.GdipDisposeImage(image); }
        }
    }

    private const int Argb = 0x26200A; // PixelFormat32bppARGB: straight alpha, BGRA in memory

    private static byte[] Draw(nint image, int size)
    {
        var pixels = new byte[size * size * 4];
        fixed (byte* scan0 = pixels)
        {
            Check(Native.GdipCreateBitmapFromScan0(size, size, size * 4, Argb, scan0, out var bitmap), "Bitmap");
            try
            {
                Check(Native.GdipGetImageGraphicsContext(bitmap, out var graphics), "Graphics");
                try
                {
                    Native.GdipSetInterpolationMode(graphics, 7); // HighQualityBicubic
                    Native.GdipSetPixelOffsetMode(graphics, 2); // HighQuality
                    Check(Native.GdipDrawImageRectI(graphics, image, 0, 0, size, size), "Draw");
                }
                finally { Native.GdipDeleteGraphics(graphics); }
            }
            finally { Native.GdipDisposeImage(bitmap); }
        }
        return pixels;
    }

    /// Blends a shape over the pixels; coverage comes from 4×4 samples per pixel.
    private static void Paint(byte[] pixels, int size, Func<double, double, bool> inside, byte red, byte green, byte blue, double opacity)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var hits = 0;
            for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
                if (inside(x + (sx + 0.5) / 4, y + (sy + 0.5) / 4)) hits++;
            if (hits == 0) continue;
            var alpha = hits / 16.0 * opacity;
            var index = (y * size + x) * 4;
            var below = pixels[index + 3] / 255.0;
            var result = alpha + below * (1 - alpha);
            byte Mix(byte top, byte bottom) => (byte)Math.Round((top * alpha + bottom * below * (1 - alpha)) / result);
            pixels[index] = Mix(blue, pixels[index]);
            pixels[index + 1] = Mix(green, pixels[index + 1]);
            pixels[index + 2] = Mix(red, pixels[index + 2]);
            pixels[index + 3] = (byte)Math.Round(result * 255);
        }
    }

    private static bool Circle(double x, double y, double centerX, double centerY, double radius) =>
        (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= radius * radius;

    private static bool RoundedRect(double x, double y, double left, double top, double width, double height, double radius)
    {
        if (x < left || y < top || x > left + width || y > top + height) return false;
        var nearestX = Math.Clamp(x, left + radius, left + width - radius);
        var nearestY = Math.Clamp(y, top + radius, top + height - radius);
        return Circle(x, y, nearestX, nearestY, radius);
    }

    /// A rectangle whose right end is a half circle: the outline of a D.
    private static bool RoundedRight(double x, double y, double left, double top, double width, double height)
    {
        if (x < left || y < top || x > left + width || y > top + height) return false;
        var radius = Math.Min(height / 2, width);
        if (x <= left + width - radius) return true;
        return Circle(x, y, left + width - radius, Math.Clamp(y, top + radius, top + height - radius), radius);
    }

    private static nint Create(byte[] pixels, int size)
    {
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var color = Native.CreateDIBSection(0, &header, 0, &bits, 0, 0);
        if (color == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint mask = 0;
        try
        {
            pixels.CopyTo(new Span<byte>(bits, pixels.Length));
            var maskBits = new byte[(size + 15) / 16 * 2 * size];
            fixed (byte* maskPointer = maskBits) mask = Native.CreateBitmap(size, size, 1, 1, maskPointer);
            var info = new Native.ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
            var icon = Native.CreateIconIndirect(&info);
            if (icon == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return icon;
        }
        finally
        {
            Native.DeleteObject(color);
            if (mask != 0) Native.DeleteObject(mask);
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"GDI+ {what} failed with status {status}.");
    }

    public void Dispose()
    {
        foreach (var icon in cache.Values) Native.DestroyIcon(icon);
        cache.Clear();
    }
}
