using Brainbox.Core.Geometry;

namespace Brainbox.Core.Capture;

/// <summary>
/// A physical display. <see cref="Bounds"/> is in virtual-desktop physical pixels;
/// <see cref="DpiScale"/> is 1.0 at 96 DPI, 1.5 at 144 DPI, etc. (per-monitor DPI).
/// </summary>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary)
{
    public bool IsPortrait => Bounds.Height > Bounds.Width;

    /// <summary>Converts a virtual-desktop pixel rectangle into this monitor's local DIPs (device independent pixels).</summary>
    public (double X, double Y, double Width, double Height) ToLocalDips(PixelRect desktopRect)
    {
        var s = DpiScale <= 0 ? 1.0 : DpiScale;
        return ((desktopRect.X - Bounds.X) / s, (desktopRect.Y - Bounds.Y) / s, desktopRect.Width / s, desktopRect.Height / s);
    }

    /// <summary>The monitor containing the centre of <paramref name="rect"/>, else the one with the largest overlap.</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, PixelRect rect)
    {
        if (monitors.Count == 0) return null;
        foreach (var m in monitors)
        {
            if (m.Bounds.Contains(rect.CenterX, rect.CenterY)) return m;
        }

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var a = m.Bounds.Intersect(rect).Area;
            if (a > bestArea)
            {
                bestArea = a;
                best = m;
            }
        }

        return best;
    }

    /// <summary>Union of all monitor bounds (the virtual desktop).</summary>
    public static PixelRect VirtualBounds(IEnumerable<MonitorInfo> monitors)
    {
        var r = PixelRect.Empty;
        foreach (var m in monitors) r = r.Union(m.Bounds);
        return r;
    }

    /// <summary>Stable signature of a monitor layout; changes when monitors are added/removed/moved or DPI changes.</summary>
    public static string LayoutSignature(IEnumerable<MonitorInfo> monitors) =>
        string.Join(";", monitors.OrderBy(m => m.DeviceName, StringComparer.Ordinal)
            .Select(m => $"{m.DeviceName}:{m.Bounds}:{m.DpiScale:0.###}"));
}
