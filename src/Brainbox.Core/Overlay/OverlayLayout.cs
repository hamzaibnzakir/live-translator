using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Core.Settings;
using Brainbox.Core.Text;

namespace Brainbox.Core.Overlay;

/// <summary>One translation to draw. Rectangles are virtual-desktop physical pixels.</summary>
public sealed record OverlayItem(
    long Id,
    string Text,
    string SourceText,
    PixelRect SourceBox,
    PixelRect Box,
    double FontSizePx,
    int BackgroundRgb,
    double BackgroundAlpha,
    int ForegroundRgb,
    OverlayStyle Style,
    string MonitorDevice,
    /// <summary>Multi-line block (vision fallback) rather than a single OCR line.</summary>
    bool IsBlock = false);

/// <summary>Input for the layout engine: a translated line and its colours.</summary>
public sealed record LayoutInput(long Id, string Translation, string Source, PixelRect SourceBox, int? BackgroundRgb, int? ForegroundRgb, bool IsBlock = false);

/// <summary>
/// Smart overlay placement (§17): decides where and how each translation is drawn so it is
/// readable — covers the original in "Replace" style (sized from the original glyph height,
/// colours sampled from the original so it blends in), or floats below/above it as a subtitle or
/// bubble — keeps it on the same monitor, and resolves collisions between neighbouring lines.
/// New styles plug in by adding a case to <see cref="Place"/>.
/// </summary>
public static class OverlayLayout
{
    private const double LineSpacing = 1.28;

    public static IReadOnlyList<OverlayItem> Layout(IReadOnlyList<LayoutInput> inputs, IReadOnlyList<MonitorInfo> monitors, BrainboxSettings settings)
    {
        var placed = new List<OverlayItem>(inputs.Count);
        foreach (var input in inputs.OrderBy(i => i.SourceBox.Y).ThenBy(i => i.SourceBox.X))
        {
            var monitor = MonitorInfo.Find(monitors, input.SourceBox);
            if (monitor == null) continue;
            var item = Place(input, monitor, settings);
            item = ResolveCollisions(item, placed, monitor);
            placed.Add(item);
        }

        return placed;
    }

    public static OverlayItem Place(LayoutInput input, MonitorInfo monitor, BrainboxSettings settings)
    {
        var scale = monitor.DpiScale <= 0 ? 1 : monitor.DpiScale;
        var src = input.SourceBox;
        var (bg, bgAlpha, fg) = Colors(input, settings);
        var mon = monitor.Bounds;

        double font;
        PixelRect box;
        switch (input.IsBlock ? OverlayStyle.Replace : settings.OverlayStyle)
        {
            case OverlayStyle.Subtitle:
            {
                font = settings.FontSize > 0 ? settings.FontSize * scale : Math.Clamp(src.Height * 0.9, 16 * scale, 34 * scale);
                var maxW = (int)(mon.Width * 0.8);
                var (w, h) = Measure(input.Translation, font, maxW);
                var x = (int)(src.CenterX - w / 2.0);
                var y = src.Bottom + (int)(4 * scale);
                if (y + h > mon.Bottom) y = src.Top - h - (int)(4 * scale);
                box = new PixelRect(x, y, w, h);
                break;
            }

            case OverlayStyle.Bubble:
            {
                font = settings.FontSize > 0 ? settings.FontSize * scale : Math.Clamp(src.Height * 0.75, 11 * scale, 26 * scale);
                var maxW = Math.Max((int)(320 * scale), src.Width);
                var (w, h) = Measure(input.Translation, font, maxW);
                var y = src.Top - h - (int)(3 * scale);
                if (y < mon.Top) y = src.Bottom + (int)(3 * scale);
                box = new PixelRect(src.X, y, w, h);
                break;
            }

            default:
            {
                // Replace: cover the original, same size as the original glyphs, grow right/down if needed.
                var glyph = input.IsBlock ? 18 * scale : src.Height;
                font = settings.FontSize > 0 ? settings.FontSize * scale : Math.Clamp(glyph * 0.78, 10 * scale, 44 * scale);
                var minW = src.Width + (int)(6 * scale);
                var room = mon.Right - (src.X - (int)(3 * scale));
                var maxW = input.IsBlock ? src.Width : Math.Max(minW, Math.Min(room, (int)Math.Max(src.Width * 1.6, 260 * scale)));

                var natural = TextWidth(input.Translation, font) + 10 * scale;
                if (!input.IsBlock && natural > maxW && settings.FontSize <= 0)
                {
                    // Shrink up to 25% before wrapping.
                    var shrink = Math.Max(0.75, maxW / natural);
                    font *= shrink;
                }

                var (w, h) = Measure(input.Translation, font, maxW);
                w = Math.Max(w, minW);
                h = Math.Max(h, src.Height + (int)(4 * scale));
                if (input.IsBlock) h = Math.Max(h, src.Height);
                box = new PixelRect(src.X - (int)(3 * scale), src.Y - (int)(2 * scale), w, h);
                break;
            }
        }

        box = ClampTo(box, mon);
        return new OverlayItem(input.Id, input.Translation, input.Source, src, box, font, bg, bgAlpha, fg,
            input.IsBlock ? OverlayStyle.Replace : settings.OverlayStyle, monitor.DeviceName, input.IsBlock);
    }

    /// <summary>Estimated rendered width in px (Segoe UI-like metrics; CJK glyphs are full-width).</summary>
    public static double TextWidth(string text, double fontPx)
    {
        double units = 0;
        foreach (var c in text)
        {
            units += c switch
            {
                ' ' => 0.28,
                'i' or 'l' or 'j' or '.' or ',' or '\'' or '!' or '|' or ':' or ';' => 0.27,
                'm' or 'w' or 'M' or 'W' => 0.85,
                _ when char.IsUpper(c) => 0.64,
                _ when LanguageId.ScriptOf(c) is Script.Han or Script.Kana or Script.Hangul => 1.0,
                _ => 0.52,
            };
        }

        return units * fontPx;
    }

    /// <summary>Box size for the text at <paramref name="fontPx"/>, wrapping at <paramref name="maxWidth"/>.</summary>
    public static (int Width, int Height) Measure(string text, double fontPx, int maxWidth)
    {
        var pad = fontPx * 0.35;
        var natural = TextWidth(text, fontPx) + pad * 2;
        var lines = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            var w = TextWidth(paragraph, fontPx) + pad * 2;
            lines += Math.Max(1, (int)Math.Ceiling(w / Math.Max(1, maxWidth)));
        }

        var width = (int)Math.Ceiling(Math.Min(natural, maxWidth));
        var height = (int)Math.Ceiling(lines * fontPx * LineSpacing + pad * 0.6);
        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static (int Bg, double Alpha, int Fg) Colors(LayoutInput input, BrainboxSettings settings)
    {
        var opacity = settings.OverlayOpacity;
        switch (settings.OverlayBackground)
        {
            case OverlayBackground.Dark:
                return (0x14161C, opacity, 0xF2F4F8);
            case OverlayBackground.Light:
                return (0xF7F7F9, opacity, 0x15171C);
            case OverlayBackground.Blur:
                return (0x1B1E26, Math.Min(opacity, 0.78), 0xFFFFFF);
            default:
            {
                // Auto: blend into the original — same background, original text colour if readable.
                var bg = input.BackgroundRgb ?? 0x14161C;
                var fg = input.ForegroundRgb ?? 0xF2F4F8;
                if (ImageOps.Contrast(bg, fg) < 4.0)
                {
                    fg = ImageOps.Luminance(bg) > 0.45 ? 0x111317 : 0xF7F8FA;
                }

                return (bg, Math.Max(opacity, 0.9), fg);
            }
        }
    }

    private static PixelRect ClampTo(PixelRect box, PixelRect mon)
    {
        var w = Math.Min(box.Width, mon.Width);
        var h = Math.Min(box.Height, mon.Height);
        var x = Math.Clamp(box.X, mon.Left, mon.Right - w);
        var y = Math.Clamp(box.Y, mon.Top, mon.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    private static OverlayItem ResolveCollisions(OverlayItem item, List<OverlayItem> placed, MonitorInfo monitor)
    {
        var box = item.Box;
        for (var guard = 0; guard < 8; guard++)
        {
            var hit = placed.FirstOrDefault(p => p.MonitorDevice == item.MonitorDevice && Overlaps(p.Box, box));
            if (hit == null) break;
            // Move just below the blocking item (lines are processed top-to-bottom).
            var y = hit.Box.Bottom + 1;
            if (y + box.Height > monitor.Bounds.Bottom) break;
            box = box with { Y = y };
        }

        return box == item.Box ? item : item with { Box = box };
    }

    private static bool Overlaps(PixelRect a, PixelRect b)
    {
        var inter = a.Intersect(b).Area;
        return inter > 0 && inter > 0.15 * Math.Min(a.Area, b.Area);
    }
}
