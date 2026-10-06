using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Brainbox.Core.Caching;
using Brainbox.Core.Capture;
using Brainbox.Core.Detection;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using Brainbox.Core.Overlay;
using Brainbox.Core.Settings;
using Brainbox.Core.Text;
using Brainbox.Core.Tracking;
using Brainbox.Core.Translation;

namespace Brainbox.Core.Pipeline;

public enum WatcherState
{
    Stopped,
    /// <summary>Monitoring and translating.</summary>
    Live,
    /// <summary>User paused translation (overlay hidden, nothing captured).</summary>
    Paused,
    /// <summary>Live translation switched off in settings.</summary>
    Disabled,
    /// <summary>Monitoring, but the translation engine is unavailable/falling back.</summary>
    Degraded,
    /// <summary>The desktop cannot be captured right now (UAC prompt, lock screen, exclusive fullscreen).</summary>
    CaptureBlocked,
}

public sealed record WatcherStatus(WatcherState State, string Message, string? SourceLanguage, string? Provider);

/// <summary>
/// The always-on engine (§3). Each tick:
/// <code>
/// capture desktop → tile change detection → settle/cap scheduling → OCR changed regions
///   → compare with tracked text → filter (English, URLs, own overlay…) → cache lookup
///   → batched, context-aware translation (background) → cache → overlay update
/// </code>
/// Every stage is isolated so a failure (OCR error, LM Studio closed, monitor unplugged, capture
/// blocked) is logged, surfaced as status, and the loop simply continues (§20).
/// </summary>
public sealed class ScreenWatcher : IDisposable
{
    private readonly IFrameSource _frames;
    private readonly IOcrEngine _ocr;
    private readonly TranslationCache _cache;
    private readonly BrainboxSettings _settings;
    private readonly ILog _log;
    private readonly Func<long> _clock;
    private readonly ChangeTracker _changes;
    private readonly ScreenTextTracker _tracker = new();
    private readonly TextFilter _filter = new();
    private readonly ContextManager _context;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly ConcurrentDictionary<(int, int), string> _regionLanguage = new();
    private readonly ConcurrentDictionary<long, long> _lineDetectedAt = new();
    private readonly object _translationGate = new();

    private ResilientTranslator _translator;
    private IVisionTranslator? _vision;
    private PerformanceProfile _profile;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Task _translationTask = Task.CompletedTask;
    private string _layoutSignature = "";
    private long _lastPublishedVersion = -1;
    private bool _forcePublish;
    private long _lastFullRescan;
    private long _lastVisionAt = -100_000;
    private int _captureFailures;
    private bool _paused;
    private IReadOnlyList<OverlayItem> _overlay = Array.Empty<OverlayItem>();
    private WatcherStatus _status = new(WatcherState.Stopped, "Stopped", null, null);
    private string? _lastSourceLanguage;

    public ScreenWatcher(IFrameSource frames, IOcrEngine ocr, ResilientTranslator translator, TranslationCache cache,
        BrainboxSettings settings, ILog? log = null, Func<long>? clockMs = null)
    {
        _frames = frames;
        _ocr = ocr;
        _translator = translator;
        _cache = cache;
        _settings = settings;
        _log = log ?? NullLog.Instance;
        var sw = Stopwatch.StartNew();
        _clock = clockMs ?? (() => sw.ElapsedMilliseconds);
        _changes = new ChangeTracker(32);
        _context = new ContextManager(settings.ContextSize);
        _profile = PerformanceProfile.From(settings);
        ApplySettings();
        _translator.StatusChanged += OnProviderStatus;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    /// <summary>Raised (on a worker thread) with the complete set of overlay items whenever it changes.</summary>
    public event Action<IReadOnlyList<OverlayItem>>? OverlayUpdated;

    /// <summary>Raised when the engine state/message changes.</summary>
    public event Action<WatcherStatus>? StatusChanged;

    /// <summary>Raised when the engine starts/finishes a burst of OCR/translation work (drives the glow pulse).</summary>
    public event Action<bool>? ActivityChanged;

    /// <summary>Raised when the monitor layout changes (overlay windows must be rebuilt).</summary>
    public event Action<IReadOnlyList<MonitorInfo>>? MonitorsChanged;

    public WatcherMetrics Metrics { get; } = new();
    public WatcherStatus Status => _status;
    public IReadOnlyList<OverlayItem> CurrentOverlay => _overlay;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public bool IsPaused => _paused;
    public ScreenTextTracker Tracker => _tracker;
    public TextFilter Filter => _filter;
    public ResilientTranslator Translator => _translator;
    public PerformanceProfile Profile => _profile;

    /// <summary>Host-provided bounds of the foreground window (for the "Current monitor" scope).</summary>
    public Func<PixelRect?>? ForegroundWindowBounds { get; set; }

    /// <summary>Swaps the translation engine (settings changed) without restarting the watcher.</summary>
    public void SetTranslator(ResilientTranslator translator, IVisionTranslator? vision = null)
    {
        _translator.StatusChanged -= OnProviderStatus;
        _translator = translator;
        _vision = vision;
        _translator.StatusChanged += OnProviderStatus;
        // Lines waiting on the old engine are retried immediately with the new one.
        _tracker.MarkRetry(_tracker.PendingReady(long.MaxValue).Select(l => l.Id), 0);
        Wake();
    }

    public void SetVisionTranslator(IVisionTranslator? vision) => _vision = vision;

    public void Start()
    {
        if (IsRunning) return;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        _runTask = Task.Run(() => RunLoopAsync(ct), ct);
        UpdateStatus();
        _log.Info("ScreenWatcher started.");
    }

    public async Task StopAsync()
    {
        var cts = _runCts;
        if (cts == null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        Wake();
        try
        {
            if (_runTask != null) await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
        _runCts = null;
        _runTask = null;
        SetOverlay(Array.Empty<OverlayItem>());
        SetStatus(new WatcherStatus(WatcherState.Stopped, "Stopped", _lastSourceLanguage, null));
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        SetOverlay(Array.Empty<OverlayItem>());
        UpdateStatus();
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        // The screen may have changed completely while paused.
        _tracker.Clear();
        _changes.Reset();
        _forcePublish = true;
        UpdateStatus();
        Wake();
    }

    /// <summary>Wakes the loop early (settings changed, resume, desktop switch hint from the host).</summary>
    public void Wake() => _wake.Release();

    /// <summary>Drops all tracked text and re-scans the whole desktop (virtual desktop switch, display change).</summary>
    public void RequestFullRescan()
    {
        _changes.Reset();
        _tracker.Clear();
        _forcePublish = true;
        Wake();
    }

    /// <summary>Waits until no translation request is in flight (tests).</summary>
    public Task WhenTranslationsIdleAsync()
    {
        lock (_translationGate) return _translationTask;
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _translator.StatusChanged -= OnProviderStatus;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _wake.Dispose();
        _tickGate.Dispose();
    }

    // ===================================================================================

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = _clock();
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die (§20).
                _log.Error("Watcher tick failed; continuing.", ex);
            }

            var active = _settings.LiveTranslationEnabled && !_paused;
            var interval = active ? _profile.IntervalMs : 1000;
            var wait = (int)Math.Max(20, interval - (_clock() - started));
            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One full pipeline iteration. Public for deterministic tests and the self-test harness.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TickCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task TickCoreAsync(CancellationToken ct)
    {
        if (!_settings.LiveTranslationEnabled || _paused)
        {
            UpdateStatus();
            return;
        }

        var now = _clock();

        // --- 1. Monitors / capture area ---------------------------------------------------
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _frames.GetMonitors();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not enumerate monitors", ex);
            return;
        }

        if (monitors.Count == 0) return;
        var sig = MonitorInfo.LayoutSignature(monitors);
        if (sig != _layoutSignature)
        {
            var first = _layoutSignature.Length == 0;
            _layoutSignature = sig;
            _changes.Reset();
            _tracker.Clear();
            _forcePublish = true;
            if (!first) _log.Info("Monitor layout changed: " + sig);
            MonitorsChanged?.Invoke(monitors);
        }

        var area = ResolveCaptureArea(monitors);
        if (area.IsEmpty) return;

        // --- 2. Capture -------------------------------------------------------------------
        var t0 = Stopwatch.GetTimestamp();
        DesktopFrame? frame;
        try
        {
            frame = _frames.Capture(area);
        }
        catch (Exception ex)
        {
            frame = null;
            if (_captureFailures == 0) _log.Warn("Desktop capture failed", ex);
        }

        if (frame == null)
        {
            _captureFailures++;
            if (_captureFailures == 3) UpdateStatus();
            return;
        }

        if (_captureFailures >= 3)
        {
            _captureFailures = 0;
            _changes.Reset(); // whatever was on screen before is stale
            UpdateStatus();
        }

        _captureFailures = 0;
        var captureMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // --- 3. Change detection ------------------------------------------------------------
        var t1 = Stopwatch.GetTimestamp();
        var masks = _frames.HonoursCaptureExclusion ? null : _overlay.Select(o => o.Box).ToList();
        if (frame.Bounds != _changes.Bounds) _tracker.Clear();
        _changes.NoiseThreshold = PerformanceProfile.NoiseThresholdFor(_settings.Sensitivity);
        _changes.Ingest(frame, now, masks);
        if (_profile.FullRescanMs > 0 && now - _lastFullRescan >= _profile.FullRescanMs)
        {
            _lastFullRescan = now;
            _changes.MarkAllDirty(now);
        }

        var regions = _changes.TakeReadyRegions(now, _profile.SettleMs, _profile.MaxWaitMs, _profile.MaxRegionsPerTick, _changes.TileSize / 2);
        var detectMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        Metrics.Tick(now, captureMs, detectMs);
        Metrics.DirtyTiles = _changes.DirtyTileCount;

        // --- 4. OCR changed regions -----------------------------------------------------------
        if (regions.Count > 0)
        {
            ActivityChanged?.Invoke(true);
            var grown = ChangeTracker.MergeOverlapping(regions.Select(r => GrowToTrackedLines(r, frame.Bounds)));
            foreach (var region in grown)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessRegionAsync(frame, region, now, ct).ConfigureAwait(false);
            }
        }

        // --- 5. Classify new text, cache lookups ---------------------------------------------
        ClassifyNewLines(now);

        // --- 6. Overlay + background translation ------------------------------------------
        PublishIfChanged(monitors);
        KickTranslation(monitors, ct);

        if (regions.Count > 0) ActivityChanged?.Invoke(false);
        Metrics.TrackedLines = _tracker.Count;
    }

    private PixelRect ResolveCaptureArea(IReadOnlyList<MonitorInfo> monitors)
    {
        switch (_settings.ScreenScope)
        {
            case ScreenScope.SpecificMonitor:
            {
                var m = monitors.FirstOrDefault(x => x.DeviceName == _settings.SpecificMonitor);
                if (m != null) return m.Bounds;
                break;
            }

            case ScreenScope.CurrentMonitor:
            {
                var fg = ForegroundWindowBounds?.Invoke();
                var m = fg is { } r ? MonitorInfo.Find(monitors, r) : null;
                m ??= monitors.FirstOrDefault(x => x.IsPrimary) ?? monitors[0];
                return m.Bounds;
            }
        }

        return MonitorInfo.VirtualBounds(monitors);
    }

    /// <summary>Grows a changed region so it contains every tracked line it touches (no half-lines).</summary>
    private PixelRect GrowToTrackedLines(PixelRect region, PixelRect bounds)
    {
        var r = region;
        for (var i = 0; i < 4; i++)
        {
            var grown = r;
            foreach (var b in _tracker.BoxesIntersecting(r)) grown = grown.Union(b.Inflate(4, 4));
            if (grown == r) break;
            r = grown;
        }

        return r.Intersect(bounds);
    }

    private async Task ProcessRegionAsync(DesktopFrame frame, PixelRect region, long now, CancellationToken ct)
    {
        var request = BuildOcrRequest(region);
        IReadOnlyList<OcrLine> lines;
        var sw = Stopwatch.StartNew();
        try
        {
            lines = await _ocr.RecognizeAsync(frame, region, request, ct).ConfigureAwait(false);

            // A line touching the left/right edge was probably cut: widen once and re-read.
            var cut = lines.Where(l => (l.Box.Left - region.Left <= 3 && region.Left > frame.Bounds.Left)
                                    || (region.Right - l.Box.Right <= 3 && region.Right < frame.Bounds.Right)).ToList();
            if (cut.Count > 0)
            {
                var lh = Math.Max(16, cut.Max(l => l.Box.Height));
                var wider = region.Inflate(Math.Max(160, lh * 12), 0).Intersect(frame.Bounds);
                if (wider != region)
                {
                    lines = await _ocr.RecognizeAsync(frame, wider, request, ct).ConfigureAwait(false);
                    region = wider;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"OCR failed for {region}; continuing.", ex);
            return;
        }
        finally
        {
            Metrics.Ocr(sw.Elapsed.TotalMilliseconds);
        }

        // Sample colours for readable overlays and drop anything our overlay displays (§16).
        var accepted = new List<OcrLine>(lines.Count);
        foreach (var l in lines)
        {
            if (_filter.IsOwnOutput(l.Text)) continue;
            var (bg, fg) = ImageOps.SampleColors(frame, l.Box);
            accepted.Add(l with { BackgroundRgb = bg, ForegroundRgb = fg });
        }

        var occluded = _frames.HonoursCaptureExclusion ? null : _overlay.Select(o => o.Box).ToList();
        var update = _tracker.ApplyRegion(region, accepted, now, occluded);
        _changes.ReportOcrOutcome(region, update.AnyChange, now, _profile.MaxCooldownMs);
        if (update.Added > 0)
        {
            foreach (var l in _tracker.TakeNew()) _lineDetectedAt.TryAdd(l.Id, now);
        }

        // Remember which recogniser worked here so the next pass tries it first.
        var lang = accepted.Select(l => l.LanguageTag).FirstOrDefault(t => t != null && LanguageId.Short(t) != LanguageId.Short(_settings.TargetLanguage));
        if (lang != null) _regionLanguage[Cell(region)] = lang;

        await MaybeVisionFallbackAsync(frame, region, accepted, now, ct).ConfigureAwait(false);
    }

    private OcrRequest BuildOcrRequest(PixelRect region)
    {
        var preferred = new List<string>();
        if (_settings.SourceLanguage != "auto")
        {
            preferred.Add(_settings.SourceLanguage);
        }
        else if (_regionLanguage.TryGetValue(Cell(region), out var hint))
        {
            preferred.Add(hint);
        }

        return new OcrRequest(preferred, _profile.ExhaustiveOcr, _profile.OcrUpscale);
    }

    private static (int, int) Cell(PixelRect r) => ((int)Math.Floor(r.CenterX / 256), (int)Math.Floor(r.CenterY / 256));

    private void ClassifyNewLines(long now)
    {
        _filter.TargetLanguage = _settings.TargetLanguage;
        _filter.SourceLanguage = _settings.SourceLanguage;
        foreach (var line in _tracker.TakeNew())
        {
            var verdict = _filter.Evaluate(line.Line.Text, line.Line.LanguageTag);
            if (verdict != FilterVerdict.Translate)
            {
                _tracker.MarkSkipped(line, verdict);
                _lineDetectedAt.TryRemove(line.Id, out _);
                continue;
            }

            if (_cache.TryGet(line.Normalized, _settings.TargetLanguage, out var cached))
            {
                Metrics.Cache(hit: true);
                if (IsEffectivelyUnchanged(line.Normalized, cached.Translation))
                {
                    _tracker.MarkSkipped(line, FilterVerdict.AlreadyTargetLanguage);
                }
                else
                {
                    _tracker.SetTranslation(line.Id, line.Normalized, cached.Translation, cached.Provider + " (cache)");
                    RecordEndToEnd(line.Id, now);
                }

                continue;
            }

            Metrics.Cache(hit: false);
            _tracker.MarkPending(line);
        }
    }

    private void KickTranslation(IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
    {
        lock (_translationGate)
        {
            if (!_translationTask.IsCompleted) return;
            if (_tracker.PendingReady(_clock()).Count == 0) return;
            _translationTask = Task.Run(() => TranslatePendingAsync(monitors, ct), ct);
        }
    }

    private async Task TranslatePendingAsync(IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
    {
        try
        {
            ActivityChanged?.Invoke(true);
            for (var round = 0; round < 20 && !ct.IsCancellationRequested; round++)
            {
                var now = _clock();
                var pending = _tracker.PendingReady(now);
                if (pending.Count == 0) break;

                // Batch lines from the same screen area (they share context), deduplicated by text.
                var anchor = pending[0];
                var area = anchor.Box.Inflate(Math.Max(300, anchor.Box.Width), 400);
                var batchLines = pending.Where(l => l.Box.IntersectsWith(area))
                    .GroupBy(l => l.Normalized)
                    .Select(g => g.First())
                    .Take(_profile.MaxBatchLines)
                    .ToList();

                // Another batch may have produced some of these meanwhile.
                var toSend = new List<TrackedLine>();
                foreach (var l in batchLines)
                {
                    if (_cache.TryGet(l.Normalized, _settings.TargetLanguage, out var c))
                        ApplyTranslation(l, c.Translation, c.Provider + " (cache)");
                    else
                        toSend.Add(l);
                }

                if (toSend.Count == 0) continue;

                var texts = toSend.Select(l => l.Normalized).ToList();
                var union = toSend.Aggregate(PixelRect.Empty, (acc, l) => acc.Union(l.Box));
                var context = _translator.PrimarySupportsContext
                    ? _context.Build(texts, _tracker.NearbyText(union, 8))
                    : Array.Empty<ContextEntry>();
                var source = _settings.SourceLanguage == "auto" ? MajorityLanguage(texts) : _settings.SourceLanguage;
                var batch = new TranslationBatch(texts, _settings.TargetLanguage, source, context);

                var outcome = await _translator.TranslateAsync(batch, ct).ConfigureAwait(false);
                Metrics.Translation(outcome.ElapsedMs, texts.Count, outcome.Success);
                if (!outcome.Success)
                {
                    var retryAt = Math.Max(_clock() + 1500, _translator.NextAvailableAtMs);
                    _tracker.MarkRetry(toSend.Select(l => l.Id), retryAt);
                    UpdateStatus();
                    break;
                }

                for (var i = 0; i < toSend.Count; i++)
                {
                    var translation = TextNormalizer.Normalize(outcome.Translations![i]);
                    if (translation.Length == 0) translation = toSend[i].Normalized;
                    _cache.Put(toSend[i].Normalized, _settings.TargetLanguage, translation, outcome.ProviderName ?? "?");
                    if (!IsEffectivelyUnchanged(toSend[i].Normalized, translation)) _context.Record(toSend[i].Normalized, translation);
                    ApplyTranslation(toSend[i], translation, outcome.ProviderName ?? "?");
                }

                if (source != null && source != "auto") _lastSourceLanguage = source;
                UpdateStatus();
                PublishIfChanged(monitors);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Translation worker failed; will retry.", ex);
        }
        finally
        {
            ActivityChanged?.Invoke(false);
        }
    }

    private void ApplyTranslation(TrackedLine line, string translation, string provider)
    {
        if (IsEffectivelyUnchanged(line.Normalized, translation))
        {
            // The model says it's already in the target language: nothing to overlay.
            _tracker.MarkSkipped(line, FilterVerdict.AlreadyTargetLanguage);
            return;
        }

        _tracker.SetTranslation(line.Id, line.Normalized, translation, provider);
        RecordEndToEnd(line.Id, _clock());
    }

    private void RecordEndToEnd(long id, long now)
    {
        if (_lineDetectedAt.TryRemove(id, out var at)) Metrics.EndToEnd(now - at);
    }

    private static bool IsEffectivelyUnchanged(string source, string translation) =>
        TextNormalizer.FuzzyKey(source) == TextNormalizer.FuzzyKey(translation);

    private static string? MajorityLanguage(IEnumerable<string> texts)
    {
        var codes = texts.Select(LanguageId.Detect).Where(g => !g.IsUndetermined).GroupBy(g => g.Code)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        return codes?.Key;
    }

    private async Task MaybeVisionFallbackAsync(DesktopFrame frame, PixelRect region, IReadOnlyList<OcrLine> lines, long now, CancellationToken ct)
    {
        if (!_profile.AllowVisionFallback || _vision == null) return;
        if (now - _lastVisionAt < 15_000) return;
        if (region.Width < 120 || region.Height < 40) return;

        // OCR found "something" but nothing usable: garbled output is the trigger.
        var verdicts = lines.Select(l => _filter.Evaluate(l.Text, l.LanguageTag)).ToList();
        if (verdicts.Count == 0 || verdicts.Any(v => v == FilterVerdict.Translate || v == FilterVerdict.AlreadyTargetLanguage)) return;
        if (!verdicts.Any(v => v is FilterVerdict.Garbled or FilterVerdict.Symbols)) return;

        _lastVisionAt = now;
        try
        {
            var crop = frame.Crop(region);
            var png = ImageOps.EncodePng(crop.Bgra, crop.Width, crop.Height);
            Metrics.Vision();
            var text = await _vision.TranslateImageAsync(png, _settings.TargetLanguage, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return;
            var line = new OcrLine("⁣vision:" + string.Join(" ", lines.Select(l => l.Text)), region, 0.5, null, now, region);
            _tracker.ApplyRegion(region, new[] { line }, now);
            var tracked = _tracker.TakeNew().FirstOrDefault(t => t.Box == region);
            if (tracked != null) _tracker.SetTranslation(tracked.Id, tracked.Normalized, text.Trim(), "vision");
        }
        catch (Exception ex)
        {
            _log.Warn("Vision fallback failed", ex);
        }
    }

    private void PublishIfChanged(IReadOnlyList<MonitorInfo> monitors)
    {
        var version = _tracker.Version;
        if (!_forcePublish && version == _lastPublishedVersion) return;
        _forcePublish = false;
        _lastPublishedVersion = version;

        var inputs = _tracker.Snapshot()
            .Where(l => l.State == LineState.Translated && !string.IsNullOrEmpty(l.Translation))
            .Select(l => new LayoutInput(l.Id, l.Translation!, l.Line.Text, l.Box, l.Line.BackgroundRgb, l.Line.ForegroundRgb,
                l.Line.Text.StartsWith('⁣')))
            .ToList();
        var items = OverlayLayout.Layout(inputs, monitors, _settings);
        SetOverlay(items);
    }

    private void SetOverlay(IReadOnlyList<OverlayItem> items)
    {
        _overlay = items;
        _filter.SetOwnOutputs(items.Select(i => i.Text));
        Metrics.OverlayItems = items.Count;
        OverlayUpdated?.Invoke(items);
    }

    private void OnProviderStatus(ProviderStatus status) => UpdateStatus();

    private void UpdateStatus()
    {
        WatcherStatus s;
        var provider = _translator.Status;
        if (!IsRunning && _runTask == null && _runCts == null) s = new WatcherStatus(WatcherState.Stopped, "Stopped", _lastSourceLanguage, null);
        else if (!_settings.LiveTranslationEnabled) s = new WatcherStatus(WatcherState.Disabled, "Live translation is off", _lastSourceLanguage, null);
        else if (_paused) s = new WatcherStatus(WatcherState.Paused, "Paused", _lastSourceLanguage, null);
        else if (_captureFailures >= 3) s = new WatcherStatus(WatcherState.CaptureBlocked, "Screen capture unavailable (secure desktop, lock screen or exclusive fullscreen) — waiting", _lastSourceLanguage, null);
        else if (provider.Health == ProviderHealth.Unavailable) s = new WatcherStatus(WatcherState.Degraded, provider.Message ?? "Translation engine unavailable — retrying", _lastSourceLanguage, provider.ActiveProvider);
        else if (provider.Health == ProviderHealth.Degraded) s = new WatcherStatus(WatcherState.Degraded, provider.Message ?? "Using fallback engine", _lastSourceLanguage, provider.ActiveProvider);
        else s = new WatcherStatus(WatcherState.Live, "Live", _lastSourceLanguage, provider.ActiveProvider);
        SetStatus(s);
    }

    private void SetStatus(WatcherStatus s)
    {
        if (s == _status) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrainboxSettings.TargetLanguage):
            case nameof(BrainboxSettings.SourceLanguage):
                _context.Clear();
                _regionLanguage.Clear();
                RequestFullRescan();
                break;
            case nameof(BrainboxSettings.PerformanceMode):
            case nameof(BrainboxSettings.DetectionIntervalMs):
            case nameof(BrainboxSettings.VisionFallback):
                _profile = PerformanceProfile.From(_settings);
                Wake();
                break;
            case nameof(BrainboxSettings.ContextSize):
                _context.Capacity = _settings.ContextSize;
                break;
            case nameof(BrainboxSettings.ScreenScope):
            case nameof(BrainboxSettings.SpecificMonitor):
                RequestFullRescan();
                break;
            case nameof(BrainboxSettings.OverlayStyle):
            case nameof(BrainboxSettings.OverlayBackground):
            case nameof(BrainboxSettings.OverlayOpacity):
            case nameof(BrainboxSettings.FontSize):
            case nameof(BrainboxSettings.FontFamily):
                _forcePublish = true;
                Wake();
                break;
            case nameof(BrainboxSettings.LiveTranslationEnabled):
                if (!_settings.LiveTranslationEnabled) SetOverlay(Array.Empty<OverlayItem>());
                else RequestFullRescan();
                UpdateStatus();
                break;
        }
    }

    private void ApplySettings()
    {
        _context.Capacity = _settings.ContextSize;
        _filter.TargetLanguage = _settings.TargetLanguage;
        _filter.SourceLanguage = _settings.SourceLanguage;
    }
}
