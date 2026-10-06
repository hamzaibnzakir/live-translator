using Brainbox.Core.Geometry;
using Brainbox.Core.Text;

namespace Brainbox.Core.Tracking;

public enum LineState
{
    /// <summary>Just seen, not yet evaluated.</summary>
    New,
    /// <summary>Should be translated, waiting for a translation.</summary>
    Pending,
    /// <summary>Has a translation; displayed.</summary>
    Translated,
    /// <summary>Not to be translated (English, URL, number...).</summary>
    Skipped,
}

/// <summary>A line of text currently believed to be on screen.</summary>
public sealed class TrackedLine
{
    internal TrackedLine(long id, OcrLine line, long now)
    {
        Id = id;
        Line = line;
        Normalized = TextNormalizer.Normalize(line.Text);
        Fuzzy = TextNormalizer.FuzzyKey(line.Text);
        FirstSeenMs = now;
        LastSeenMs = now;
    }

    public long Id { get; }
    public OcrLine Line { get; internal set; }
    public string Normalized { get; internal set; }
    internal string Fuzzy { get; set; }
    public LineState State { get; internal set; }
    public FilterVerdict Verdict { get; internal set; }
    public string? Translation { get; internal set; }
    public string? Provider { get; internal set; }
    public long FirstSeenMs { get; }
    public long LastSeenMs { get; internal set; }
    /// <summary>Earliest time a failed translation may be retried.</summary>
    public long RetryAtMs { get; internal set; }
    public int Attempts { get; internal set; }
    public PixelRect Box => Line.Box;
}

/// <summary>Outcome of applying fresh OCR results for one region.</summary>
public sealed record RegionUpdate(int Added, int Removed, int Moved, int Unchanged)
{
    /// <summary>True when the region's set of texts changed (drives the change tracker's cool-down).</summary>
    public bool TextChanged => Added > 0 || Removed > 0;
    public bool AnyChange => Added > 0 || Removed > 0 || Moved > 0;
}

/// <summary>
/// The engine's model of "which text is on screen where". OCR results for a region replace the
/// lines previously inside that region; unchanged text keeps its identity (and translation) even
/// when it moves, so window moves and scrolling re-use translations instead of re-requesting them,
/// and text that disappears takes its overlay with it (§10).
/// </summary>
public sealed class ScreenTextTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<long, TrackedLine> _lines = new();
    private long _nextId = 1;
    private long _version;

    /// <summary>Incremented on every visible change; the overlay re-renders when it moves.</summary>
    public long Version { get { lock (_gate) return _version; } }

    public int Count { get { lock (_gate) return _lines.Count; } }

    public IReadOnlyList<TrackedLine> Snapshot()
    {
        lock (_gate) return _lines.Values.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_lines.Count == 0) return;
            _lines.Clear();
            _version++;
        }
    }

    /// <summary>Lines whose box intersects <paramref name="region"/> (used to grow OCR regions to whole lines).</summary>
    public IReadOnlyList<PixelRect> BoxesIntersecting(PixelRect region)
    {
        lock (_gate) return _lines.Values.Where(l => l.Box.IntersectsWith(region)).Select(l => l.Box).ToList();
    }

    /// <summary>
    /// Replaces the tracked content of <paramref name="region"/> with <paramref name="lines"/>.
    /// A tracked line is "inside" the region when its centre lies in it or the region covers most of it.
    /// </summary>
    /// <param name="occluded">
    /// Areas the capture cannot see through (our own overlay when the OS does not exclude it from
    /// capture). Tracked lines hidden under them are kept as they are instead of being dropped.
    /// </param>
    public RegionUpdate ApplyRegion(PixelRect region, IReadOnlyList<OcrLine> lines, long nowMs, IReadOnlyList<PixelRect>? occluded = null)
    {
        lock (_gate)
        {
            var affected = _lines.Values
                .Where(l => region.Contains(l.Box.CenterX, l.Box.CenterY) || l.Box.CoveredBy(region) >= 0.5)
                .Where(l => occluded == null || !occluded.Any(o => l.Box.CoveredBy(o) >= 0.5))
                .ToList();
            var unmatched = new HashSet<long>(affected.Select(a => a.Id));
            int added = 0, moved = 0, unchanged = 0;

            foreach (var line in lines)
            {
                var fuzzy = TextNormalizer.FuzzyKey(line.Text);
                if (fuzzy.Length == 0) continue;

                // Exact text match first (prefer the closest box), then OCR-jitter tolerant match.
                var match = affected.Where(a => unmatched.Contains(a.Id) && a.Fuzzy == fuzzy)
                                .OrderByDescending(a => a.Box.IoU(line.Box))
                                .ThenBy(a => Distance(a.Box, line.Box))
                                .FirstOrDefault()
                            ?? affected.Where(a => unmatched.Contains(a.Id) && a.Box.IoU(line.Box) >= 0.5
                                                   && TextNormalizer.Similarity(a.Normalized, line.Text) >= 0.88)
                                .OrderByDescending(a => a.Box.IoU(line.Box))
                                .FirstOrDefault();

                if (match != null)
                {
                    unmatched.Remove(match.Id);
                    var wasMoved = match.Box != line.Box && match.Box.IoU(line.Box) < 0.9;
                    // Keep the original text identity for jitter matches so the translation stays valid.
                    match.Line = match.Fuzzy == fuzzy ? line : line with { Text = match.Line.Text };
                    match.LastSeenMs = nowMs;
                    if (wasMoved)
                    {
                        moved++;
                        _version++;
                    }
                    else
                    {
                        unchanged++;
                    }

                    continue;
                }

                var tracked = new TrackedLine(_nextId++, line, nowMs) { State = LineState.New };
                _lines[tracked.Id] = tracked;
                added++;
                _version++;
            }

            foreach (var id in unmatched)
            {
                _lines.Remove(id);
                _version++;
            }

            return new RegionUpdate(added, unmatched.Count, moved, unchanged);
        }
    }

    /// <summary>Lines that have not been classified yet.</summary>
    public IReadOnlyList<TrackedLine> TakeNew()
    {
        lock (_gate) return _lines.Values.Where(l => l.State == LineState.New).ToList();
    }

    /// <summary>Lines waiting for a translation whose retry time has come.</summary>
    public IReadOnlyList<TrackedLine> PendingReady(long nowMs)
    {
        lock (_gate)
            return _lines.Values.Where(l => l.State == LineState.Pending && l.RetryAtMs <= nowMs)
                .OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
    }

    public void MarkSkipped(TrackedLine line, FilterVerdict verdict)
    {
        lock (_gate)
        {
            line.State = LineState.Skipped;
            line.Verdict = verdict;
        }
    }

    public void MarkPending(TrackedLine line)
    {
        lock (_gate)
        {
            line.State = LineState.Pending;
            line.Verdict = FilterVerdict.Translate;
        }
    }

    public void MarkRetry(IEnumerable<long> ids, long retryAtMs)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_lines.TryGetValue(id, out var l) && l.State == LineState.Pending)
                {
                    l.Attempts++;
                    l.RetryAtMs = retryAtMs;
                }
            }
        }
    }

    /// <summary>
    /// Stores a translation for the line with this id and for every other on-screen line with the
    /// same text (the same label is often shown several times). Returns true if anything changed.
    /// </summary>
    public bool SetTranslation(long id, string normalizedSource, string translation, string provider)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var l in _lines.Values)
            {
                if (l.Id != id && l.Normalized != normalizedSource) continue;
                if (l.State == LineState.Translated && l.Translation == translation) continue;
                l.Translation = translation;
                l.Provider = provider;
                l.State = LineState.Translated;
                changed = true;
            }

            if (changed) _version++;
            return changed;
        }
    }

    /// <summary>Text of tracked lines near <paramref name="area"/> (context for translation), top to bottom.</summary>
    public IReadOnlyList<string> NearbyText(PixelRect area, int maxLines)
    {
        lock (_gate)
        {
            var grown = area.Inflate(Math.Max(80, area.Width / 2), Math.Max(80, area.Height));
            return _lines.Values
                .Where(l => l.Box.IntersectsWith(grown) && l.State != LineState.Skipped || l.Box.IntersectsWith(area))
                .OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X)
                .Select(l => l.Normalized)
                .Distinct()
                .Take(maxLines)
                .ToList();
        }
    }

    private static double Distance(PixelRect a, PixelRect b)
    {
        var dx = a.CenterX - b.CenterX;
        var dy = a.CenterY - b.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
