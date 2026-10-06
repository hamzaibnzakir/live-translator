using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;

namespace Brainbox.Core.Text;

/// <summary>One recognised line of text. Box/SourceRegion are virtual-desktop pixels.</summary>
public sealed record OcrLine(
    string Text,
    PixelRect Box,
    double Confidence,
    string? LanguageTag,
    long TimestampMs,
    PixelRect SourceRegion)
{
    /// <summary>Average glyph height in pixels — used to size the overlay font.</summary>
    public int GlyphHeight => Box.Height;

    /// <summary>Dominant background colour behind the text as 0xRRGGBB (sampled from the frame), if known.</summary>
    public int? BackgroundRgb { get; init; }

    /// <summary>Dominant text colour as 0xRRGGBB, if known.</summary>
    public int? ForegroundRgb { get; init; }
}

/// <summary>Hints for one OCR call.</summary>
public sealed record OcrRequest(
    /// <summary>BCP-47 recognizer tags to use, in preference order. Empty = engine decides (auto).</summary>
    IReadOnlyList<string> PreferredLanguageTags,
    /// <summary>Try every installed script family instead of stopping at the first plausible one.</summary>
    bool ExhaustiveLanguageSearch,
    /// <summary>Upscale factor for small text (1 = none).</summary>
    double UpscaleFactor);

/// <summary>Text recognition over a desktop region.</summary>
public interface IOcrEngine
{
    string Name { get; }

    /// <summary>True when at least one recognizer is usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Tags of installed recognizers (e.g. "en-US", "ja-JP").</summary>
    IReadOnlyList<string> InstalledLanguageTags { get; }

    Task<IReadOnlyList<OcrLine>> RecognizeAsync(DesktopFrame frame, PixelRect region, OcrRequest request, CancellationToken cancellationToken);
}
