namespace Brainbox.Core.Pipeline;

/// <summary>Live performance numbers (§22 test 12). Times are exponential moving averages in ms.</summary>
public sealed class WatcherMetrics
{
    private const double Alpha = 0.2;
    private readonly object _gate = new();

    public long Ticks { get; private set; }
    public double AvgTickIntervalMs { get; private set; }
    public double AvgCaptureMs { get; private set; }
    public double AvgDetectMs { get; private set; }
    public double AvgOcrMs { get; private set; }
    public double MaxOcrMs { get; private set; }
    public long OcrCalls { get; private set; }
    public double AvgTranslationMs { get; private set; }
    public long TranslationRequests { get; private set; }
    public long TranslationFailures { get; private set; }
    public long LinesTranslated { get; private set; }
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public long VisionCalls { get; private set; }
    public int TrackedLines { get; internal set; }
    public int OverlayItems { get; internal set; }
    public int DirtyTiles { get; internal set; }
    /// <summary>Time from a text change being detected to its translation appearing (EMA).</summary>
    public double AvgEndToEndMs { get; private set; }

    private long _lastTickAt = -1;

    internal void Tick(long nowMs, double captureMs, double detectMs)
    {
        lock (_gate)
        {
            Ticks++;
            if (_lastTickAt >= 0) AvgTickIntervalMs = Ema(AvgTickIntervalMs, nowMs - _lastTickAt, Ticks <= 2);
            _lastTickAt = nowMs;
            AvgCaptureMs = Ema(AvgCaptureMs, captureMs, Ticks == 1);
            AvgDetectMs = Ema(AvgDetectMs, detectMs, Ticks == 1);
        }
    }

    internal void Ocr(double ms)
    {
        lock (_gate)
        {
            OcrCalls++;
            AvgOcrMs = Ema(AvgOcrMs, ms, OcrCalls == 1);
            MaxOcrMs = Math.Max(MaxOcrMs, ms);
        }
    }

    internal void Translation(double ms, int lines, bool success)
    {
        lock (_gate)
        {
            TranslationRequests++;
            if (!success)
            {
                TranslationFailures++;
                return;
            }

            LinesTranslated += lines;
            AvgTranslationMs = Ema(AvgTranslationMs, ms, TranslationRequests - TranslationFailures == 1);
        }
    }

    internal void EndToEnd(double ms)
    {
        lock (_gate) AvgEndToEndMs = Ema(AvgEndToEndMs, ms, AvgEndToEndMs == 0);
    }

    internal void Cache(bool hit)
    {
        lock (_gate)
        {
            if (hit) CacheHits++;
            else CacheMisses++;
        }
    }

    internal void Vision()
    {
        lock (_gate) VisionCalls++;
    }

    private static double Ema(double current, double sample, bool first) => first ? sample : current + Alpha * (sample - current);

    public override string ToString() =>
        $"ticks={Ticks} interval={AvgTickIntervalMs:0}ms capture={AvgCaptureMs:0.0}ms detect={AvgDetectMs:0.0}ms " +
        $"ocr={AvgOcrMs:0}ms (max {MaxOcrMs:0}, n={OcrCalls}) translate={AvgTranslationMs:0}ms (req={TranslationRequests}, fail={TranslationFailures}, lines={LinesTranslated}) " +
        $"cache hit/miss={CacheHits}/{CacheMisses} e2e={AvgEndToEndMs:0}ms tracked={TrackedLines} overlay={OverlayItems}";
}
