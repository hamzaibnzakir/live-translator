using System.Runtime.InteropServices;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;

namespace Brainbox.Core.Detection;

/// <summary>
/// Layer 1 of the pipeline: cheap pixel change detection + scheduling.
///
/// The desktop is split into square tiles. Every frame each tile gets a 64-bit content hash (all
/// pixels, read as 64-bit words — a few ms for a 4K desktop). Tiles whose hash changed are then
/// checked against a tiny luminance thumbnail so sub-threshold noise (dithering, compression
/// shimmer) is ignored according to the sensitivity setting.
///
/// Changed tiles become "dirty". A dirty tile is handed to OCR once it has settled (no change for
/// <c>settleMs</c>) or, for areas that never stop changing (video under subtitles, games), once it
/// has been dirty for <c>maxWaitMs</c> — so continuously animating areas are sampled at a capped
/// rate instead of every frame. Areas whose OCR text keeps coming back identical (blinking caret,
/// clocks, spinners) get an exponential cool-down so they stop costing CPU.
/// </summary>
public sealed class ChangeTracker
{
    private const int ThumbCells = 4; // 4x4 luminance thumbnail per tile

    private readonly int _tileSize;
    private PixelRect _bounds = PixelRect.Empty;
    private int _cols;
    private int _rows;

    private ulong[] _hashes = Array.Empty<ulong>();
    private byte[] _thumbs = Array.Empty<byte>();
    private bool[] _known = Array.Empty<bool>();      // tile has a baseline hash/thumbnail
    private long[] _dirtySince = Array.Empty<long>(); // -1 = clean
    private long[] _lastChange = Array.Empty<long>();
    private long[] _coolUntil = Array.Empty<long>();
    private int[] _flaps = Array.Empty<int>();

    private ulong[] _scratchHash = Array.Empty<ulong>();

    public ChangeTracker(int tileSize = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 8);
        _tileSize = tileSize;
    }

    public int TileSize => _tileSize;
    public PixelRect Bounds => _bounds;
    public int DirtyTileCount { get; private set; }
    public int LastChangedTileCount { get; private set; }

    /// <summary>Mean absolute luminance difference (0..255) a tile thumbnail cell must exceed to count as a change.</summary>
    public int NoiseThreshold { get; set; } = 6;

    /// <summary>Forget everything (monitor layout change, virtual-desktop switch, resume after pause).</summary>
    public void Reset()
    {
        _bounds = PixelRect.Empty;
        _cols = _rows = 0;
        DirtyTileCount = 0;
    }

    /// <summary>Marks every tile of the frame dirty (forces a full re-scan on the next schedule).</summary>
    public void MarkAllDirty(long nowMs)
    {
        for (var i = 0; i < _dirtySince.Length; i++)
        {
            if (_dirtySince[i] < 0) _dirtySince[i] = nowMs;
            _lastChange[i] = nowMs - 100_000; // treat as already settled
            _coolUntil[i] = 0;
            _flaps[i] = 0;
        }

        DirtyTileCount = _dirtySince.Length;
    }

    /// <summary>
    /// Ingests a frame and returns how many tiles changed. <paramref name="masks"/> are desktop
    /// rectangles to ignore (our own overlay when the OS cannot exclude it from capture).
    /// </summary>
    public int Ingest(DesktopFrame frame, long nowMs, IReadOnlyList<PixelRect>? masks = null)
    {
        if (frame.Bounds != _bounds) Allocate(frame.Bounds, nowMs);

        ComputeHashes(frame);

        var changed = 0;
        for (var ty = 0; ty < _rows; ty++)
        {
            for (var tx = 0; tx < _cols; tx++)
            {
                var i = ty * _cols + tx;
                if (_known[i] && _scratchHash[i] == _hashes[i]) continue;

                var tile = TileRect(tx, ty);
                if (masks != null && IsMasked(tile, masks))
                {
                    _hashes[i] = _scratchHash[i];
                    continue;
                }

                var significant = UpdateThumb(frame, tx, ty, i, out _);
                _hashes[i] = _scratchHash[i];
                var wasKnown = _known[i];
                _known[i] = true;
                if (!significant && wasKnown) continue;

                changed++;
                _lastChange[i] = nowMs;
                if (_dirtySince[i] < 0)
                {
                    _dirtySince[i] = nowMs;
                    DirtyTileCount++;
                }
            }
        }

        LastChangedTileCount = changed;
        return changed;
    }

    /// <summary>
    /// Returns desktop regions that are ready for OCR and clears their dirty flags.
    /// Regions are connected groups of ready tiles (8-neighbourhood, bridged across one-tile gaps so
    /// neighbouring text lines stay together), inflated by <paramref name="marginPx"/>.
    /// </summary>
    public IReadOnlyList<PixelRect> TakeReadyRegions(long nowMs, int settleMs, int maxWaitMs, int maxRegions, int marginPx)
    {
        if (_cols == 0 || DirtyTileCount == 0) return Array.Empty<PixelRect>();

        var n = _cols * _rows;
        var ready = new bool[n];
        var any = false;
        for (var i = 0; i < n; i++)
        {
            var since = _dirtySince[i];
            if (since < 0 || nowMs < _coolUntil[i]) continue;
            if (nowMs - _lastChange[i] >= settleMs || nowMs - since >= maxWaitMs)
            {
                ready[i] = true;
                any = true;
            }
        }

        if (!any) return Array.Empty<PixelRect>();

        var regions = ConnectedRegions(ready);
        regions.Sort((a, b) => b.Area.CompareTo(a.Area));

        var result = new List<PixelRect>();
        foreach (var r in regions)
        {
            if (result.Count >= maxRegions) break;
            var inflated = r.Inflate(marginPx, marginPx).Intersect(_bounds);
            result.Add(inflated);
            ClearDirty(r);
        }

        return MergeOverlapping(result);
    }

    /// <summary>
    /// Feedback from OCR: when a region produced the same text as before, the area is "flapping"
    /// (caret, clock, animation) and gets an exponential cool-down capped at <paramref name="maxCooldownMs"/>.
    /// </summary>
    public void ReportOcrOutcome(PixelRect region, bool textChanged, long nowMs, int maxCooldownMs)
    {
        ForEachTile(region, i =>
        {
            if (textChanged)
            {
                _flaps[i] = 0;
                _coolUntil[i] = 0;
            }
            else
            {
                _flaps[i] = Math.Min(_flaps[i] + 1, 8);
                var cool = Math.Min(maxCooldownMs, 125 << _flaps[i]);
                _coolUntil[i] = nowMs + cool;
            }
        });
    }

    // ---------------------------------------------------------------------------------------

    private void Allocate(PixelRect bounds, long nowMs)
    {
        _bounds = bounds;
        _cols = (bounds.Width + _tileSize - 1) / _tileSize;
        _rows = (bounds.Height + _tileSize - 1) / _tileSize;
        var n = _cols * _rows;
        _hashes = new ulong[n];
        _scratchHash = new ulong[n];
        _thumbs = new byte[n * ThumbCells * ThumbCells];
        _known = new bool[n];
        _dirtySince = new long[n];
        _lastChange = new long[n];
        _coolUntil = new long[n];
        _flaps = new int[n];
        Array.Fill(_dirtySince, -1);
        Array.Fill(_lastChange, nowMs);
        DirtyTileCount = 0;
    }

    private PixelRect TileRect(int tx, int ty)
    {
        var x = _bounds.X + tx * _tileSize;
        var y = _bounds.Y + ty * _tileSize;
        return PixelRect.FromLTRB(x, y, Math.Min(x + _tileSize, _bounds.Right), Math.Min(y + _tileSize, _bounds.Bottom));
    }

    private static bool IsMasked(PixelRect tile, IReadOnlyList<PixelRect> masks)
    {
        foreach (var m in masks)
        {
            if (tile.CoveredBy(m) >= 0.6) return true;
        }

        return false;
    }

    private void ComputeHashes(DesktopFrame frame)
    {
        const ulong prime = 0x100000001B3UL;
        const ulong seed = 0xCBF29CE484222325UL;
        Array.Fill(_scratchHash, seed);

        var px = frame.Bgra;
        var stride = frame.Stride;
        var width = frame.Width;
        var height = frame.Height;
        var words = MemoryMarshal.Cast<byte, ulong>(px.AsSpan());

        for (var y = 0; y < height; y++)
        {
            var ty = y / _tileSize;
            var rowBase = ty * _cols;
            var rowByte = y * stride;
            for (var tx = 0; tx < _cols; tx++)
            {
                var x0 = tx * _tileSize;
                var x1 = Math.Min(x0 + _tileSize, width);
                var startByte = rowByte + x0 * 4;
                var endByte = rowByte + x1 * 4;
                var h = _scratchHash[rowBase + tx];

                // Aligned 8-byte words where possible, tail bytes individually.
                var b = startByte;
                if ((b & 7) == 0)
                {
                    var w0 = b >> 3;
                    var wn = endByte >> 3;
                    for (var w = w0; w < wn; w++)
                    {
                        h = (h ^ words[w]) * prime;
                        h ^= h >> 29;
                    }

                    b = wn << 3;
                }

                for (; b < endByte; b++)
                {
                    h = (h ^ px[b]) * prime;
                }

                _scratchHash[rowBase + tx] = h;
            }
        }
    }

    /// <summary>Recomputes the tile thumbnail; returns true when it differs enough from the previous one.</summary>
    private bool UpdateThumb(DesktopFrame frame, int tx, int ty, int index, out int maxDiff)
    {
        var tile = TileRect(tx, ty);
        var lx = tile.X - frame.Bounds.X;
        var ly = tile.Y - frame.Bounds.Y;
        var cw = Math.Max(1, tile.Width / ThumbCells);
        var ch = Math.Max(1, tile.Height / ThumbCells);
        var px = frame.Bgra;
        var stride = frame.Stride;
        var baseIdx = index * ThumbCells * ThumbCells;
        maxDiff = 0;
        var totalDiff = 0;

        for (var cy = 0; cy < ThumbCells; cy++)
        {
            for (var cx = 0; cx < ThumbCells; cx++)
            {
                var x0 = lx + cx * cw;
                var y0 = ly + cy * ch;
                var x1 = Math.Min(x0 + cw, lx + tile.Width);
                var y1 = Math.Min(y0 + ch, ly + tile.Height);
                int sum = 0, count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * stride;
                    for (var x = x0; x < x1; x++)
                    {
                        var o = row + x * 4;
                        // ITU-R BT.601 luma, integer approximation.
                        sum += (px[o + 2] * 77 + px[o + 1] * 150 + px[o] * 29) >> 8;
                        count++;
                    }
                }

                var lum = (byte)(count == 0 ? 0 : sum / count);
                var k = baseIdx + cy * ThumbCells + cx;
                var d = Math.Abs(lum - _thumbs[k]);
                totalDiff += d;
                if (d > maxDiff) maxDiff = d;
                _thumbs[k] = lum;
            }
        }

        // Any single cell moving a lot (a glyph appeared) or a broad faint change both count.
        return maxDiff > NoiseThreshold * 2 || totalDiff > NoiseThreshold * ThumbCells * ThumbCells / 2;
    }

    private void ClearDirty(PixelRect desktopRect)
    {
        ForEachTile(desktopRect, i =>
        {
            if (_dirtySince[i] >= 0)
            {
                _dirtySince[i] = -1;
                DirtyTileCount--;
            }
        });
    }

    private void ForEachTile(PixelRect desktopRect, Action<int> action)
    {
        var r = desktopRect.Intersect(_bounds);
        if (r.IsEmpty || _cols == 0) return;
        var tx0 = (r.Left - _bounds.X) / _tileSize;
        var ty0 = (r.Top - _bounds.Y) / _tileSize;
        var tx1 = (r.Right - 1 - _bounds.X) / _tileSize;
        var ty1 = (r.Bottom - 1 - _bounds.Y) / _tileSize;
        for (var ty = ty0; ty <= ty1; ty++)
        {
            for (var tx = tx0; tx <= tx1; tx++)
            {
                action(ty * _cols + tx);
            }
        }
    }

    private List<PixelRect> ConnectedRegions(bool[] ready)
    {
        var n = ready.Length;
        var label = new int[n];
        var result = new List<PixelRect>();
        var stack = new Stack<int>();
        var current = 0;

        for (var start = 0; start < n; start++)
        {
            if (!ready[start] || label[start] != 0) continue;
            current++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            stack.Push(start);
            label[start] = current;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var tx = i % _cols;
                var ty = i / _cols;
                minX = Math.Min(minX, tx);
                maxX = Math.Max(maxX, tx);
                minY = Math.Min(minY, ty);
                maxY = Math.Max(maxY, ty);

                // Neighbourhood radius 2 bridges single-tile gaps (spaces between words / lines).
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var nx = tx + dx;
                        var ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= _cols || ny >= _rows) continue;
                        var j = ny * _cols + nx;
                        if (!ready[j] || label[j] != 0) continue;
                        label[j] = current;
                        stack.Push(j);
                    }
                }
            }

            var tl = TileRect(minX, minY);
            var br = TileRect(maxX, maxY);
            result.Add(PixelRect.FromLTRB(tl.Left, tl.Top, br.Right, br.Bottom));
        }

        return result;
    }

    /// <summary>Merges rectangles that overlap so the same pixels are never OCR'd twice in one tick.</summary>
    public static List<PixelRect> MergeOverlapping(IEnumerable<PixelRect> rects)
    {
        var list = rects.Where(r => !r.IsEmpty).ToList();
        bool merged;
        do
        {
            merged = false;
            for (var i = 0; i < list.Count && !merged; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (!list[i].IntersectsWith(list[j])) continue;
                    list[i] = list[i].Union(list[j]);
                    list.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        } while (merged);

        return list;
    }
}
