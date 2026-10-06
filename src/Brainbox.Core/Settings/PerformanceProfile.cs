namespace Brainbox.Core.Settings;

/// <summary>
/// Concrete tuning numbers for a <see cref="PerformanceMode"/> (§4).
/// </summary>
public sealed record PerformanceProfile(
    PerformanceMode Mode,
    /// <summary>Time between desktop captures.</summary>
    int IntervalMs,
    /// <summary>A changed area must be still this long before OCR (lets scrolling/typing finish).</summary>
    int SettleMs,
    /// <summary>Areas that never stop changing (video, games) are still OCR'd at least this often.</summary>
    int MaxWaitMs,
    /// <summary>Upper bound for the exponential cool-down of areas whose text keeps coming back unchanged.</summary>
    int MaxCooldownMs,
    /// <summary>Max OCR regions per tick (bounds worst-case CPU per tick).</summary>
    int MaxRegionsPerTick,
    /// <summary>Try every installed script family per region instead of stopping at the first plausible result.</summary>
    bool ExhaustiveOcr,
    /// <summary>Full-desktop re-scan period (0 = only on change).</summary>
    int FullRescanMs,
    /// <summary>Upscale factor applied to small text before OCR.</summary>
    double OcrUpscale,
    /// <summary>Lines per translation request.</summary>
    int MaxBatchLines,
    bool AllowVisionFallback)
{
    public static PerformanceProfile For(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Performance => new(mode, IntervalMs: 250, SettleMs: 120, MaxWaitMs: 500, MaxCooldownMs: 1000,
            MaxRegionsPerTick: 10, ExhaustiveOcr: false, FullRescanMs: 0, OcrUpscale: 1.0, MaxBatchLines: 12, AllowVisionFallback: false),
        PerformanceMode.BatterySaver => new(mode, IntervalMs: 1500, SettleMs: 600, MaxWaitMs: 3000, MaxCooldownMs: 4000,
            MaxRegionsPerTick: 3, ExhaustiveOcr: false, FullRescanMs: 0, OcrUpscale: 1.0, MaxBatchLines: 20, AllowVisionFallback: false),
        PerformanceMode.MaximumAccuracy => new(mode, IntervalMs: 400, SettleMs: 200, MaxWaitMs: 800, MaxCooldownMs: 1500,
            MaxRegionsPerTick: 12, ExhaustiveOcr: true, FullRescanMs: 20000, OcrUpscale: 1.6, MaxBatchLines: 12, AllowVisionFallback: true),
        _ => new(PerformanceMode.Balanced, IntervalMs: 500, SettleMs: 220, MaxWaitMs: 1200, MaxCooldownMs: 2000,
            MaxRegionsPerTick: 6, ExhaustiveOcr: false, FullRescanMs: 0, OcrUpscale: 1.25, MaxBatchLines: 12, AllowVisionFallback: false),
    };

    /// <summary>Profile for the user's settings (mode + optional interval override + sensitivity).</summary>
    public static PerformanceProfile From(BrainboxSettings s)
    {
        var p = For(s.PerformanceMode);
        if (s.DetectionIntervalMs > 0) p = p with { IntervalMs = s.DetectionIntervalMs };
        if (!s.VisionFallback) p = p with { AllowVisionFallback = false };
        return p;
    }

    /// <summary>Maps sensitivity 1..10 to the change detector's noise threshold (luminance units).</summary>
    public static int NoiseThresholdFor(int sensitivity) => Math.Clamp(22 - sensitivity * 2, 2, 20);
}
