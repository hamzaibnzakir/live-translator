using Brainbox.Core.Geometry;

namespace Brainbox.Core.Capture;

/// <summary>
/// One captured frame of (part of) the virtual desktop as 32-bit BGRA pixels.
/// <see cref="Bounds"/> is the area the frame covers in virtual-desktop coordinates, so pixel
/// (0,0) of the buffer is the virtual-desktop point (Bounds.X, Bounds.Y).
/// </summary>
public sealed class DesktopFrame
{
    public DesktopFrame(PixelRect bounds, byte[] bgra, int stride, long timestampMs, IReadOnlyList<MonitorInfo> monitors)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Frame bounds are empty.", nameof(bounds));
        if (stride < bounds.Width * 4) throw new ArgumentException("Stride too small.", nameof(stride));
        if (bgra.Length < stride * bounds.Height) throw new ArgumentException("Pixel buffer too small.", nameof(bgra));
        Bounds = bounds;
        Bgra = bgra;
        Stride = stride;
        TimestampMs = timestampMs;
        Monitors = monitors;
    }

    public PixelRect Bounds { get; }
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public int Stride { get; }
    public byte[] Bgra { get; }
    public long TimestampMs { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Copies a desktop-space region out of the frame as a tightly packed BGRA buffer.</summary>
    public CroppedImage Crop(PixelRect desktopRegion)
    {
        var r = desktopRegion.Intersect(Bounds);
        if (r.IsEmpty) return new CroppedImage(PixelRect.Empty, Array.Empty<byte>(), 0, 0);

        var w = r.Width;
        var h = r.Height;
        var dst = new byte[w * h * 4];
        var srcX = r.X - Bounds.X;
        var srcY = r.Y - Bounds.Y;
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(Bgra, (srcY + row) * Stride + srcX * 4, dst, row * w * 4, w * 4);
        }

        return new CroppedImage(r, dst, w, h);
    }

    /// <summary>Monitor whose bounds contain the centre of <paramref name="rect"/> (or the most overlapped one).</summary>
    public MonitorInfo? MonitorFor(PixelRect rect) => MonitorInfo.Find(Monitors, rect);
}

/// <summary>A tightly packed BGRA image cut from a frame; <see cref="DesktopRect"/> is where it came from.</summary>
public sealed record CroppedImage(PixelRect DesktopRect, byte[] Bgra, int Width, int Height)
{
    public bool IsEmpty => Width == 0 || Height == 0;
}
