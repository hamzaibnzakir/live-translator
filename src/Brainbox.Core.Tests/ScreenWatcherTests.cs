using Brainbox.Core.Caching;
using Brainbox.Core.Capture;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using Brainbox.Core.Overlay;
using Brainbox.Core.Pipeline;
using Brainbox.Core.Settings;
using Brainbox.Core.Translation;
using Xunit;

namespace Brainbox.Core.Tests;

/// <summary>
/// End-to-end tests of the always-on pipeline on a synthetic desktop: change detection → OCR →
/// filtering → cache → context-aware translation → tracking → overlay layout.
/// </summary>
public class ScreenWatcherTests
{
    private sealed class Rig : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public FakeDesktop Desk { get; }
        public FakeOcr Ocr { get; }
        public FakeProvider Provider { get; }
        public BrainboxSettings Settings { get; } = new();
        public TranslationCache Cache { get; }
        public ScreenWatcher Watcher { get; }
        public MemoryLog Log { get; } = new();
        public List<IReadOnlyList<OverlayItem>> Updates { get; } = new();

        public Rig(FakeDesktop? desk = null, TranslationCache? cache = null, FakeProvider? provider = null, params ITranslationProvider[] extra)
        {
            Desk = desk ?? new FakeDesktop();
            Ocr = new FakeOcr(Desk);
            Provider = provider ?? new FakeProvider("LM Studio", new Dictionary<string, string>
            {
                ["こんにちは世界"] = "Hello world",
                ["ようこそ"] = "Welcome",
                ["Bonjour tout le monde"] = "Hello everyone",
                ["さようなら"] = "Goodbye",
            });
            Cache = cache ?? new TranslationCache();
            var chain = new ResilientTranslator(new ITranslationProvider[] { Provider }.Concat(extra), Clock.Read, Log);
            Watcher = new ScreenWatcher(Desk, Ocr, chain, Cache, Settings, Log, Clock.Read);
            Watcher.OverlayUpdated += items =>
            {
                lock (Updates) Updates.Add(items);
            };
        }

        public async Task Ticks(int count, int stepMs = 300)
        {
            for (var i = 0; i < count; i++)
            {
                Clock.Advance(stepMs);
                await Watcher.TickAsync(CancellationToken.None);
                await Watcher.WhenTranslationsIdleAsync();
            }
        }

        public IReadOnlyList<OverlayItem> Overlay => Watcher.CurrentOverlay;

        public void Dispose()
        {
            Watcher.Dispose();
            Cache.Dispose();
        }
    }

    [Fact]
    public async Task Foreign_text_is_detected_translated_and_placed_over_the_original_automatically()
    {
        using var rig = new Rig();
        var jp = new PixelRect(100, 100, 140, 24);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", jp));
        rig.Desk.Texts.Add(new FakeText("Save your changes", new PixelRect(100, 300, 160, 20), "en-US"));

        await rig.Ticks(4);

        var item = Assert.Single(rig.Overlay);
        Assert.Equal("Hello world", item.Text);
        Assert.True(item.Box.Contains(jp.Inflate(-1, -1)), $"overlay {item.Box} must cover {jp}");
        Assert.Equal("DISPLAY1", item.MonitorDevice);
        // English was never sent for translation.
        Assert.DoesNotContain(rig.Provider.Requests.SelectMany(r => r.Lines), l => l.Contains("Save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_text_is_not_retranslated_for_minutes()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        Assert.Equal(1, rig.Provider.LinesRequested);
        var ocrCalls = rig.Ocr.Calls;

        await rig.Ticks(600, 500); // five simulated minutes
        Assert.Equal(1, rig.Provider.LinesRequested);
        Assert.Equal(ocrCalls, rig.Ocr.Calls); // static screen → not even OCR'd again
        Assert.Single(rig.Overlay);
    }

    [Fact]
    public async Task Overlay_follows_moved_text_without_new_translation_requests()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        Assert.Single(rig.Overlay);

        rig.Desk.Texts.Clear();
        var moved = new PixelRect(380, 260, 140, 24);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", moved));
        await rig.Ticks(4);

        var item = Assert.Single(rig.Overlay);
        Assert.True(item.Box.Contains(moved.Inflate(-1, -1)), $"overlay {item.Box} should follow to {moved}");
        Assert.Equal(1, rig.Provider.LinesRequested);
    }

    [Fact]
    public async Task Overlay_disappears_when_source_text_disappears_and_updates_when_it_changes()
    {
        using var rig = new Rig();
        var box = new PixelRect(100, 100, 140, 24);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", box));
        await rig.Ticks(4);
        Assert.Equal("Hello world", Assert.Single(rig.Overlay).Text);

        rig.Desk.Texts[0] = new FakeText("さようなら", box);
        await rig.Ticks(4);
        Assert.Equal("Goodbye", Assert.Single(rig.Overlay).Text);

        rig.Desk.Texts.Clear();
        await rig.Ticks(4);
        Assert.Empty(rig.Overlay);
    }

    [Fact]
    public async Task Overlay_never_gets_translated_even_if_the_os_leaks_it_into_the_capture()
    {
        var desk = new FakeDesktop { HonoursCaptureExclusion = false };
        using var rig = new Rig(desk);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        rig.Watcher.OverlayUpdated += items => desk.OverlayToLeak = items; // the overlay "appears" in the next frames
        await rig.Ticks(4);
        Assert.Single(rig.Overlay);

        // Move the overlay off the source to make the leaked English text fully visible to "OCR".
        desk.OverlayToLeak = rig.Overlay.Select(o => o with { Box = o.Box.Offset(0, 200) }).ToList();
        await rig.Ticks(20);

        var allLines = rig.Provider.Requests.SelectMany(r => r.Lines).ToList();
        Assert.Equal(new[] { "こんにちは世界" }, allLines);
        Assert.DoesNotContain(rig.Overlay, o => o.SourceText == "Hello world");
    }

    [Fact]
    public async Task Survives_translation_engine_outage_and_recovers()
    {
        var provider = new FakeProvider("LM Studio", new Dictionary<string, string> { ["こんにちは世界"] = "Hello world" }) { Down = true };
        using var rig = new Rig(provider: provider);
        var states = new List<WatcherState>();
        rig.Watcher.StatusChanged += s => states.Add(s.State);
        rig.Watcher.Start(); // status tracking requires the running state
        await rig.Watcher.StopAsync();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));

        await rig.Ticks(4);
        Assert.Empty(rig.Overlay);
        Assert.Equal(ProviderHealth.Unavailable, rig.Watcher.Translator.Status.Health);

        provider.Down = false; // user starts LM Studio again
        await rig.Ticks(30, 500);
        Assert.Equal("Hello world", Assert.Single(rig.Overlay).Text);
        Assert.Equal(ProviderHealth.Healthy, rig.Watcher.Translator.Status.Health);
        Assert.InRange(provider.Requests.Count, 2, 8); // backed off, not hammered every tick
    }

    [Fact]
    public async Task Recovers_even_when_translator_and_watcher_use_different_clocks()
    {
        // Regression (found on the Windows runner): the app's translator used Environment.TickCount64
        // while the watcher used its own stopwatch, so retries were scheduled days in the future.
        var clock = new FakeClock();
        var desk = new FakeDesktop();
        var provider = new FakeProvider("LM Studio", new Dictionary<string, string> { ["こんにちは世界"] = "Hello world" }) { Down = true };
        var translatorClock = new FakeClock { Now = 900_000_000 };
        var chain = new ResilientTranslator(new[] { provider }, () => translatorClock.Now);
        using var cache = new TranslationCache();
        using var watcher = new ScreenWatcher(desk, new FakeOcr(desk), chain, cache, new BrainboxSettings(), null, clock.Read);
        desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(300);
            translatorClock.Now += 300;
            await watcher.TickAsync(CancellationToken.None);
            await watcher.WhenTranslationsIdleAsync();
        }

        provider.Down = false;
        for (var i = 0; i < 40 && watcher.CurrentOverlay.Count == 0; i++)
        {
            clock.Advance(500);
            translatorClock.Now += 500;
            await watcher.TickAsync(CancellationToken.None);
            await watcher.WhenTranslationsIdleAsync();
        }

        Assert.Equal("Hello world", Assert.Single(watcher.CurrentOverlay).Text);
    }

    [Fact]
    public async Task Falls_back_to_secondary_engine_when_local_ai_is_down()
    {
        var local = new FakeProvider("LM Studio") { Down = true };
        var cloud = new FakeProvider("Google", new Dictionary<string, string> { ["こんにちは世界"] = "Hello, world" }, supportsContext: false);
        using var rig = new Rig(provider: local, extra: cloud);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        Assert.Equal("Hello, world", Assert.Single(rig.Overlay).Text);
        Assert.Equal(ProviderHealth.Degraded, rig.Watcher.Translator.Status.Health);
    }

    [Fact]
    public async Task Ocr_failures_do_not_stop_the_watcher()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        rig.Ocr.Throw = true;
        await rig.Ticks(4);
        Assert.Empty(rig.Overlay);
        rig.Ocr.Throw = false;
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(300, 300, 80, 24)));
        await rig.Ticks(4);
        Assert.Contains(rig.Overlay, o => o.Text == "Welcome");
    }

    [Fact]
    public async Task Blocked_capture_is_reported_and_recovers()
    {
        using var rig = new Rig();
        rig.Watcher.Start();
        await rig.Watcher.StopAsync();
        rig.Desk.Blocked = true;
        await rig.Ticks(5);
        rig.Desk.Blocked = false;
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(300, 300, 80, 24)));
        await rig.Ticks(4);
        Assert.Equal("Welcome", Assert.Single(rig.Overlay).Text);
    }

    [Fact]
    public async Task Pause_hides_overlay_and_stops_capturing_resume_rescans()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        Assert.Single(rig.Overlay);

        rig.Watcher.Pause();
        Assert.Empty(rig.Overlay);
        var captures = rig.Desk.CaptureCalls;
        await rig.Ticks(10);
        Assert.Equal(captures, rig.Desk.CaptureCalls);

        rig.Watcher.Resume();
        await rig.Ticks(4);
        Assert.Single(rig.Overlay);
        Assert.Equal(1, rig.Provider.LinesRequested); // came from cache
    }

    [Fact]
    public async Task Disabling_live_translation_clears_overlay()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        rig.Settings.LiveTranslationEnabled = false;
        Assert.Empty(rig.Overlay);
        rig.Settings.LiveTranslationEnabled = true;
        await rig.Ticks(4);
        Assert.Single(rig.Overlay);
    }

    [Fact]
    public async Task Multiple_monitors_with_negative_coordinates_are_all_monitored()
    {
        var left = new MonitorInfo("LEFT", new PixelRect(-800, 0, 800, 600), new PixelRect(-800, 0, 800, 560), 1.5, false);
        var main = new MonitorInfo("MAIN", new PixelRect(0, 0, 640, 480), new PixelRect(0, 0, 640, 440), 1.0, true);
        using var rig = new Rig(new FakeDesktop(left, main));
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(-600, 200, 140, 24)));
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(200, 100, 80, 24)));
        await rig.Ticks(4);
        Assert.Equal(2, rig.Overlay.Count);
        Assert.Equal("LEFT", rig.Overlay.Single(o => o.Text == "Hello world").MonitorDevice);
        Assert.Equal("MAIN", rig.Overlay.Single(o => o.Text == "Welcome").MonitorDevice);
    }

    [Fact]
    public async Task Monitor_hotplug_triggers_rescan_and_event()
    {
        var main = new MonitorInfo("MAIN", new PixelRect(0, 0, 640, 480), new PixelRect(0, 0, 640, 440), 1.0, true);
        var desk = new FakeDesktop(main);
        using var rig = new Rig(desk);
        var events = 0;
        rig.Watcher.MonitorsChanged += _ => events++;
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        lock (desk) desk.Monitors.Add(new MonitorInfo("NEW", new PixelRect(640, 0, 640, 480), new PixelRect(640, 0, 640, 440), 1.0, false));
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(800, 100, 80, 24)));
        await rig.Ticks(4);
        Assert.Equal(2, events);
        Assert.Equal(2, rig.Overlay.Count);
    }

    [Fact]
    public async Task Specific_monitor_scope_only_watches_that_monitor()
    {
        var left = new MonitorInfo("LEFT", new PixelRect(-800, 0, 800, 600), new PixelRect(-800, 0, 800, 560), 1.0, false);
        var main = new MonitorInfo("MAIN", new PixelRect(0, 0, 640, 480), new PixelRect(0, 0, 640, 440), 1.0, true);
        using var rig = new Rig(new FakeDesktop(left, main));
        rig.Settings.ScreenScope = ScreenScope.SpecificMonitor;
        rig.Settings.SpecificMonitor = "MAIN";
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(-600, 200, 140, 24)));
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(200, 100, 80, 24)));
        await rig.Ticks(4);
        Assert.Equal("Welcome", Assert.Single(rig.Overlay).Text);
    }

    [Fact]
    public async Task Context_from_previous_subtitles_is_sent_with_the_next_line()
    {
        using var rig = new Rig();
        var sub = new PixelRect(150, 400, 300, 28);
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", sub));
        await rig.Ticks(4);
        rig.Desk.Texts[0] = new FakeText("さようなら", sub);
        await rig.Ticks(4);

        var last = rig.Provider.Requests.Last();
        Assert.Equal(new[] { "さようなら" }, last.Lines);
        Assert.Contains(last.Context, c => c.Source == "こんにちは世界" && c.Translation == "Hello world");
    }

    [Fact]
    public async Task Persistent_cache_means_zero_requests_after_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbx-watch-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "cache.db");
        try
        {
            using (var rig1 = new Rig(cache: new TranslationCache(new SqliteTranslationStore(path))))
            {
                rig1.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
                await rig1.Ticks(4);
                Assert.Equal(1, rig1.Provider.LinesRequested);
            }

            using var rig2 = new Rig(cache: new TranslationCache(new SqliteTranslationStore(path)));
            rig2.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
            await rig2.Ticks(4);
            Assert.Equal("Hello world", Assert.Single(rig2.Overlay).Text);
            Assert.Equal(0, rig2.Provider.LinesRequested);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Changing_target_language_retranslates()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        await rig.Ticks(4);
        rig.Settings.TargetLanguage = "fr";
        await rig.Ticks(4);
        Assert.Equal("fr", rig.Provider.Requests.Last().TargetLanguage);
        Assert.Single(rig.Overlay);
    }

    [Fact]
    public async Task Same_label_shown_twice_is_translated_once()
    {
        using var rig = new Rig();
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(100, 100, 80, 24)));
        rig.Desk.Texts.Add(new FakeText("ようこそ", new PixelRect(100, 300, 80, 24), "ja-JP "));
        await rig.Ticks(4);
        Assert.Equal(2, rig.Overlay.Count);
        Assert.Equal(1, rig.Provider.LinesRequested);
    }

    [Fact]
    public async Task Run_loop_works_with_real_clock()
    {
        var desk = new FakeDesktop();
        desk.Texts.Add(new FakeText("こんにちは世界", new PixelRect(100, 100, 140, 24)));
        var provider = new FakeProvider("LM Studio", new Dictionary<string, string> { ["こんにちは世界"] = "Hello world" });
        var settings = new BrainboxSettings { PerformanceMode = PerformanceMode.Performance };
        using var cache = new TranslationCache();
        using var watcher = new ScreenWatcher(desk, new FakeOcr(desk), new ResilientTranslator(new[] { provider }, () => Environment.TickCount64), cache, settings);
        var got = new TaskCompletionSource<IReadOnlyList<OverlayItem>>();
        watcher.OverlayUpdated += items =>
        {
            if (items.Count > 0) got.TrySetResult(items);
        };
        watcher.Start();
        var winner = await Task.WhenAny(got.Task, Task.Delay(10_000));
        await watcher.StopAsync();
        Assert.Same(got.Task, winner);
        Assert.Equal("Hello world", (await got.Task).Single().Text);
        Assert.True(watcher.Metrics.Ticks > 0);
    }
}
