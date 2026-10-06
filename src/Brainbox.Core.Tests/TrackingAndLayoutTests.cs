using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Core.Overlay;
using Brainbox.Core.Settings;
using Brainbox.Core.Text;
using Brainbox.Core.Tracking;
using Xunit;

namespace Brainbox.Core.Tests;

public class ScreenTextTrackerTests
{
    private static OcrLine L(string t, int x, int y, int w = 100, int h = 20) => new(t, new PixelRect(x, y, w, h), 1, "ja-JP", 0, PixelRect.Empty);

    [Fact]
    public void Adds_keeps_moves_and_removes_lines()
    {
        var tr = new ScreenTextTracker();
        var region = new PixelRect(0, 0, 500, 500);
        var u = tr.ApplyRegion(region, new[] { L("こんにちは", 10, 10), L("さようなら", 10, 50) }, 0);
        Assert.Equal(2, u.Added);
        var ids = tr.Snapshot().Select(l => l.Id).ToList();

        // Same content → unchanged, identities kept.
        u = tr.ApplyRegion(region, new[] { L("こんにちは", 10, 10), L("さようなら", 10, 50) }, 1);
        Assert.False(u.AnyChange);
        Assert.Equal(ids, tr.Snapshot().Select(l => l.Id).ToList());

        // Window moved → same lines, new boxes, still same identities.
        u = tr.ApplyRegion(region, new[] { L("こんにちは", 210, 110), L("さようなら", 210, 150) }, 2);
        Assert.Equal(2, u.Moved);
        Assert.Equal(0, u.Added);
        Assert.Equal(ids, tr.Snapshot().Select(l => l.Id).ToList());
        Assert.Equal(new PixelRect(210, 110, 100, 20), tr.Snapshot()[0].Box);

        // One line disappears.
        u = tr.ApplyRegion(region, new[] { L("こんにちは", 210, 110) }, 3);
        Assert.Equal(1, u.Removed);
        Assert.Equal(1, tr.Count);
    }

    [Fact]
    public void Lines_outside_region_are_untouched()
    {
        var tr = new ScreenTextTracker();
        tr.ApplyRegion(new PixelRect(0, 0, 1000, 1000), new[] { L("左", 10, 10), L("右", 800, 10) }, 0);
        tr.ApplyRegion(new PixelRect(0, 0, 400, 400), Array.Empty<OcrLine>(), 1);
        Assert.Equal("右", tr.Snapshot().Single().Normalized);
    }

    [Fact]
    public void Ocr_jitter_keeps_identity_and_translation()
    {
        var tr = new ScreenTextTracker();
        var region = new PixelRect(0, 0, 500, 500);
        tr.ApplyRegion(region, new[] { L("Bonjour tout le monde", 10, 10) }, 0);
        var line = tr.Snapshot()[0];
        tr.SetTranslation(line.Id, line.Normalized, "Hello everyone", "test");
        var u = tr.ApplyRegion(region, new[] { L("Bonjour tout le rnonde", 11, 10) }, 1);
        Assert.False(u.TextChanged);
        var after = tr.Snapshot()[0];
        Assert.Equal(line.Id, after.Id);
        Assert.Equal("Hello everyone", after.Translation);
    }

    [Fact]
    public void Translation_applies_to_all_lines_with_same_text()
    {
        var tr = new ScreenTextTracker();
        tr.ApplyRegion(new PixelRect(0, 0, 500, 500), new[] { L("設定", 10, 10), L("設定", 10, 300) }, 0);
        var first = tr.Snapshot()[0];
        Assert.True(tr.SetTranslation(first.Id, first.Normalized, "Settings", "p"));
        Assert.All(tr.Snapshot(), l => Assert.Equal("Settings", l.Translation));
    }

    [Fact]
    public void Pending_retry_respects_time()
    {
        var tr = new ScreenTextTracker();
        tr.ApplyRegion(new PixelRect(0, 0, 500, 500), new[] { L("設定", 10, 10) }, 0);
        var l = tr.TakeNew().Single();
        tr.MarkPending(l);
        Assert.Single(tr.PendingReady(0));
        tr.MarkRetry(new[] { l.Id }, 5000);
        Assert.Empty(tr.PendingReady(4999));
        Assert.Single(tr.PendingReady(5000));
    }
}

public class OverlayLayoutTests
{
    private static readonly MonitorInfo Primary = new("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);
    private static readonly MonitorInfo LeftHiDpi = new("\\\\.\\DISPLAY2", new PixelRect(-2560, -200, 2560, 1440), new PixelRect(-2560, -200, 2560, 1400), 1.5, false);
    private static readonly MonitorInfo Portrait = new("\\\\.\\DISPLAY3", new PixelRect(1920, -500, 1080, 1920), new PixelRect(1920, -500, 1080, 1880), 1.25, false);

    [Fact]
    public void Replace_style_covers_original_and_matches_glyph_size()
    {
        var s = new BrainboxSettings();
        var item = OverlayLayout.Place(new LayoutInput(1, "Hello world", "こんにちは世界", new PixelRect(100, 100, 120, 24), 0xFFFFFF, 0x000000), Primary, s);
        Assert.True(item.Box.Contains(new PixelRect(100, 100, 120, 24).Inflate(-1, -1)), $"{item.Box} must cover the source");
        Assert.InRange(item.FontSizePx, 15, 22);
        Assert.Equal(0xFFFFFF, item.BackgroundRgb);  // blends into the white page
        Assert.Equal(0x000000, item.ForegroundRgb);
    }

    [Fact]
    public void Auto_colours_fix_unreadable_contrast()
    {
        var s = new BrainboxSettings();
        var item = OverlayLayout.Place(new LayoutInput(1, "Hi", "やあ", new PixelRect(10, 10, 40, 20), 0x202020, 0x252525), Primary, s);
        Assert.True(ImageOps.Contrast(item.BackgroundRgb, item.ForegroundRgb) >= 4.0);
    }

    [Fact]
    public void Items_stay_on_their_monitor_even_with_negative_coordinates()
    {
        var s = new BrainboxSettings();
        var inputs = new[]
        {
            new LayoutInput(1, "A long translated sentence that will need more room than the original", "短い", new PixelRect(-120, 600, 100, 30), null, null),
            new LayoutInput(2, "Portrait monitor text", "縦", new PixelRect(2900, 1300, 90, 28), null, null),
        };
        var items = OverlayLayout.Layout(inputs, new[] { Primary, LeftHiDpi, Portrait }, s);
        var a = items.Single(i => i.Id == 1);
        Assert.Equal(LeftHiDpi.DeviceName, a.MonitorDevice);
        Assert.True(LeftHiDpi.Bounds.Contains(a.Box), $"{a.Box} escaped its monitor");
        var b = items.Single(i => i.Id == 2);
        Assert.Equal(Portrait.DeviceName, b.MonitorDevice);
        Assert.True(Portrait.Bounds.Contains(b.Box));
    }

    [Fact]
    public void Dpi_scale_is_respected_for_fixed_font_size()
    {
        var s = new BrainboxSettings { FontSize = 16 };
        var item = OverlayLayout.Place(new LayoutInput(1, "Hello", "やあ", new PixelRect(-1000, 100, 60, 30), null, null), LeftHiDpi, s);
        Assert.Equal(24, item.FontSizePx, 3); // 16 DIP at 150 %
        var dips = LeftHiDpi.ToLocalDips(item.Box);
        Assert.Equal((item.Box.X + 2560) / 1.5, dips.X, 3);
    }

    [Fact]
    public void Overlapping_items_are_pushed_apart()
    {
        var s = new BrainboxSettings();
        var inputs = new[]
        {
            new LayoutInput(1, "A very long translation that wraps onto two or more lines for sure because it is long", "一", new PixelRect(100, 100, 80, 20), null, null),
            new LayoutInput(2, "Second", "二", new PixelRect(100, 122, 80, 20), null, null),
        };
        var items = OverlayLayout.Layout(inputs, new[] { Primary }, s);
        var first = items.Single(i => i.Id == 1).Box;
        var second = items.Single(i => i.Id == 2).Box;
        Assert.True(first.Intersect(second).Area <= 0.15 * Math.Min(first.Area, second.Area));
    }

    [Theory]
    [InlineData(OverlayStyle.Subtitle)]
    [InlineData(OverlayStyle.Bubble)]
    public void Floating_styles_do_not_cover_source(OverlayStyle style)
    {
        var s = new BrainboxSettings { OverlayStyle = style };
        var src = new PixelRect(500, 500, 200, 30);
        var item = OverlayLayout.Place(new LayoutInput(1, "Translated", "原文", src, null, null), Primary, s);
        Assert.False(item.Box.IntersectsWith(src), $"{style} box {item.Box} overlaps the source {src}");
    }

    [Fact]
    public void Monitor_lookup_and_layout_signature()
    {
        var monitors = new[] { Primary, LeftHiDpi };
        Assert.Equal(LeftHiDpi, MonitorInfo.Find(monitors, new PixelRect(-50, 50, 10, 10)));
        Assert.Equal(Primary, MonitorInfo.Find(monitors, new PixelRect(5, 5, 10, 10)));
        Assert.Equal(new PixelRect(-2560, -200, 4480, 1440), MonitorInfo.VirtualBounds(monitors));
        Assert.NotEqual(MonitorInfo.LayoutSignature(monitors), MonitorInfo.LayoutSignature(new[] { Primary }));
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_survives_corrupt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbx-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var s = new BrainboxSettings { TargetLanguage = "fr", PerformanceMode = PerformanceMode.BatterySaver, GlowIntensity = 0.8, Model = "qwen" };
            s.Save(path);
            var loaded = BrainboxSettings.Load(path);
            Assert.Equal("fr", loaded.TargetLanguage);
            Assert.Equal(PerformanceMode.BatterySaver, loaded.PerformanceMode);
            Assert.Equal(0.8, loaded.GlowIntensity);
            Assert.Equal("qwen", loaded.Model);

            File.WriteAllText(path, "{ not json");
            var fallback = BrainboxSettings.Load(path);
            Assert.Equal("en", fallback.TargetLanguage);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Clamps_values_and_raises_change_notifications()
    {
        var s = new BrainboxSettings();
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        s.Sensitivity = 99;
        s.GlowIntensity = -1;
        s.TargetLanguage = "";
        Assert.Equal(10, s.Sensitivity);
        Assert.Equal(0.05, s.GlowIntensity);
        Assert.Equal("en", s.TargetLanguage);
        Assert.Contains(nameof(BrainboxSettings.Sensitivity), changed);
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var s = new BrainboxSettings();
        Assert.Equal("en", s.TargetLanguage);
        Assert.Equal("auto", s.SourceLanguage);
        Assert.Equal("http://localhost:1234/v1", s.LmStudioEndpoint);
        Assert.Equal("Ctrl+Alt+B", s.HotkeyToggle);
        Assert.Equal("Ctrl+Alt+P", s.HotkeyPause);
        Assert.Equal("Ctrl+Alt+G", s.HotkeyGlow);
        Assert.Equal(ScreenScope.AllMonitors, s.ScreenScope);
        Assert.True(s.GlowEnabled);
    }

    [Theory]
    [InlineData(PerformanceMode.Performance)]
    [InlineData(PerformanceMode.Balanced)]
    [InlineData(PerformanceMode.BatterySaver)]
    [InlineData(PerformanceMode.MaximumAccuracy)]
    public void Performance_profiles_are_ordered_sensibly(PerformanceMode mode)
    {
        var p = PerformanceProfile.For(mode);
        Assert.True(p.IntervalMs >= 100);
        Assert.True(p.MaxWaitMs >= p.SettleMs);
        if (mode == PerformanceMode.BatterySaver) Assert.True(p.IntervalMs > PerformanceProfile.For(PerformanceMode.Balanced).IntervalMs);
        if (mode == PerformanceMode.Performance) Assert.True(p.IntervalMs < PerformanceProfile.For(PerformanceMode.Balanced).IntervalMs);
        if (mode == PerformanceMode.MaximumAccuracy) Assert.True(p.ExhaustiveOcr);
    }
}
