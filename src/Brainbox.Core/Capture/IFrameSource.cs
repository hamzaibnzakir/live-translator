using Brainbox.Core.Geometry;

namespace Brainbox.Core.Capture;

/// <summary>
/// Supplies desktop frames. The Windows implementation captures the whole virtual desktop (or a
/// selected subset of monitors) of whichever Windows virtual desktop is currently visible.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Current monitor layout (re-queried on every call so hot-plug / DPI changes are seen).</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Captures <paramref name="area"/> (virtual-desktop coordinates). Returns null when capture is
    /// temporarily impossible (secure desktop / UAC prompt, locked workstation, display mode switch).
    /// </summary>
    DesktopFrame? Capture(PixelRect area);

    /// <summary>
    /// True when windows marked as "exclude from capture" (our overlay + glow) are guaranteed to be
    /// absent from captured frames. When false the pipeline masks overlay areas itself.
    /// </summary>
    bool HonoursCaptureExclusion { get; }
}
