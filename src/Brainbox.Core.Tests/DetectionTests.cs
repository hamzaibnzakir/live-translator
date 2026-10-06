using Brainbox.Core.Capture;
using Brainbox.Core.Detection;
using Brainbox.Core.Geometry;
using Xunit;

namespace Brainbox.Core.Tests;

public class ChangeTrackerTests
{
    private static (FakeDesktop Desk, PixelRect Area) Desk()
    {
        var d = new FakeDesktop();
        return (d, new PixelRect(0, 0, 640, 480));
    }

    [Fact]
    public void First_frame_marks_everything_dirty_and_waits_for_settle()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        Assert.Equal(20 * 15, t.DirtyTileCount);
        Assert.Empty(t.TakeReadyRegions(100, settleMs: 200, maxWaitMs: 1000, maxRegions: 10, marginPx: 0));
        var regions = t.TakeReadyRegions(250, 200, 1000, 10, 0);
        Assert.Single(regions);
        Assert.Equal(area, regions[0]);
        Assert.Equal(0, t.DirtyTileCount);
    }

    [Fact]
    public void Static_screen_produces_no_work()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        t.TakeReadyRegions(1000, 200, 1000, 10, 0);
        for (var i = 1; i <= 20; i++)
        {
            Assert.Equal(0, t.Ingest(desk.Capture(area)!, 1000 + i * 100));
        }

        Assert.Empty(t.TakeReadyRegions(5000, 200, 1000, 10, 0));
    }

    [Fact]
    public void Small_change_yields_small_region_around_it()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        t.TakeReadyRegions(1000, 200, 1000, 10, 0);

        desk.Texts.Add(new FakeText("新しい", new PixelRect(300, 200, 50, 20)));
        Assert.True(t.Ingest(desk.Capture(area)!, 1100) > 0);
        var regions = t.TakeReadyRegions(1400, 200, 1000, 10, 16);
        Assert.Single(regions);
        var r = regions[0];
        Assert.True(r.Contains(new PixelRect(300, 200, 50, 20)), $"region {r} must contain the change");
        Assert.True(r.Area < area.Area / 10, $"region {r} should be small");
    }

    [Fact]
    public void Continuously_changing_area_is_sampled_at_max_wait()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        t.TakeReadyRegions(1000, 200, 1000, 10, 0);

        var now = 1000L;
        var produced = 0;
        for (var i = 0; i < 30; i++)
        {
            now += 100;
            desk.Texts.Clear();
            desk.Texts.Add(new FakeText("frame" + i, new PixelRect(100 + i % 3, 100, 60, 30))); // "video" never settles
            t.Ingest(desk.Capture(area)!, now);
            produced += t.TakeReadyRegions(now, settleMs: 200, maxWaitMs: 1000, maxRegions: 10, marginPx: 0).Count;
        }

        // 3 seconds of constant change with maxWait=1s → a handful of OCR passes (edge tiles whose
        // sub-threshold change lets them settle may add a few), never one per frame.
        Assert.InRange(produced, 2, 8);
    }

    [Fact]
    public void Masked_areas_are_ignored()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        t.TakeReadyRegions(1000, 200, 1000, 10, 0);
        desk.Texts.Add(new FakeText("overlay", new PixelRect(64, 64, 64, 64)));
        var changed = t.Ingest(desk.Capture(area)!, 1100, new[] { new PixelRect(60, 60, 72, 72) });
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Flapping_area_gets_cooldown()
    {
        var (desk, area) = Desk();
        var t = new ChangeTracker(32);
        t.Ingest(desk.Capture(area)!, 0);
        t.TakeReadyRegions(1000, 0, 0, 10, 0);
        var caret = new PixelRect(200, 200, 2, 18);
        var now = 1000L;
        var ocrPasses = 0;
        for (var i = 0; i < 40; i++)
        {
            now += 100;
            desk.Texts.Clear();
            if (i % 2 == 0) desk.Texts.Add(new FakeText("|", caret)); // blinking caret
            t.Ingest(desk.Capture(area)!, now);
            foreach (var r in t.TakeReadyRegions(now, 0, 0, 10, 0))
            {
                ocrPasses++;
                t.ReportOcrOutcome(r, textChanged: false, now, maxCooldownMs: 2000);
            }
        }

        Assert.True(ocrPasses < 15, $"caret blinking caused {ocrPasses} OCR passes");
    }

    [Fact]
    public void Merge_overlapping()
    {
        var merged = ChangeTracker.MergeOverlapping(new[] { new PixelRect(0, 0, 10, 10), new PixelRect(5, 5, 10, 10), new PixelRect(100, 100, 5, 5) });
        Assert.Equal(2, merged.Count);
        Assert.Contains(new PixelRect(0, 0, 15, 15), merged);
    }

    [Fact]
    public void Reallocates_when_frame_bounds_change()
    {
        var t = new ChangeTracker(32);
        var desk = new FakeDesktop();
        t.Ingest(desk.Capture(new PixelRect(0, 0, 640, 480))!, 0);
        t.Ingest(desk.Capture(new PixelRect(-1920, 0, 2560, 1080))!, 10);
        Assert.Equal(new PixelRect(-1920, 0, 2560, 1080), t.Bounds);
    }
}

public class ImageOpsTests
{
    [Fact]
    public void Samples_background_and_foreground()
    {
        var desk = new FakeDesktop();
        desk.Texts.Add(new FakeText("x", new PixelRect(110, 105, 30, 10)));
        var frame = desk.Capture(new PixelRect(0, 0, 640, 480))!;
        var (bg, fg) = ImageOps.SampleColors(frame, new PixelRect(100, 100, 50, 20));
        Assert.Equal(0x404040, bg);
        Assert.Equal(desk.Palette.Keys.Single(), fg);
    }

    [Fact]
    public void Png_has_signature_and_resize_keeps_size()
    {
        var px = new byte[4 * 4 * 4];
        var png = ImageOps.EncodePng(px, 4, 4);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        var big = ImageOps.Resize(px, 4, 4, 8, 6);
        Assert.Equal(8 * 6 * 4, big.Length);
    }

    [Fact]
    public void Contrast_ratio()
    {
        Assert.Equal(21.0, ImageOps.Contrast(0xFFFFFF, 0x000000), 1);
        Assert.Equal(1.0, ImageOps.Contrast(0x777777, 0x777777), 3);
    }

    [Fact]
    public void Crop_copies_region_in_desktop_coordinates()
    {
        var desk = new FakeDesktop(new MonitorInfo("L", new PixelRect(-100, 0, 100, 50), new PixelRect(-100, 0, 100, 50), 1, false));
        desk.Texts.Add(new FakeText("x", new PixelRect(-50, 10, 10, 10)));
        var frame = desk.Capture(new PixelRect(-100, 0, 100, 50))!;
        var crop = frame.Crop(new PixelRect(-50, 10, 10, 10));
        Assert.Equal(10, crop.Width);
        Assert.NotEqual(0x40, crop.Bgra[0]);
    }
}
