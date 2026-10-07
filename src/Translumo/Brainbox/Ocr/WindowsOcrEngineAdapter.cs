using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Core.Text;
using global::Windows.Globalization;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
using global::Windows.Security.Cryptography;
using OcrLine = Brainbox.Core.Text.OcrLine;

namespace Brainbox.Desktop.Ocr
{
    /// <summary>
    /// Positional OCR with the built-in Windows OCR engine (Windows.Media.Ocr): no extra download,
    /// runs locally, returns per-word boxes which are merged into line boxes.
    ///
    /// Source language "Automatic": Windows OCR needs a recognizer per writing system, so one
    /// recognizer per installed script family is tried (Latin, Japanese, Chinese, Korean, Cyrillic,
    /// Arabic…). The family that worked last time for the same screen area is tried first and the
    /// search stops early when its output is clean; otherwise results are scored by how well the
    /// text matches each recognizer's script and merged line-by-line (so a Japanese page with
    /// English words keeps both).
    /// </summary>
    public sealed class WindowsOcrEngineAdapter : IOcrEngine
    {
        private readonly ConcurrentDictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1); // Windows OCR is not designed for heavy parallel use

        public string Name => "Windows OCR";

        public bool IsAvailable => InstalledLanguageTags.Count > 0;

        public IReadOnlyList<string> InstalledLanguageTags =>
            OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

        public static uint MaxDimension => OcrEngine.MaxImageDimension;

        public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(DesktopFrame frame, PixelRect region, OcrRequest request, CancellationToken cancellationToken)
        {
            var crop = frame.Crop(region);
            if (crop.IsEmpty || crop.Width < 8 || crop.Height < 8) return Array.Empty<OcrLine>();

            // Upscale small text areas: Windows OCR struggles below ~10px glyphs.
            var scale = request.UpscaleFactor > 1.01 && (long)crop.Width * crop.Height < 2_500_000 ? request.UpscaleFactor : 1.0;
            var max = (int)MaxDimension;
            if (crop.Width * scale > max || crop.Height * scale > max) scale = 1.0;

            var tiles = SplitForMaxDimension(crop, max);
            var families = CandidateTags(request);
            if (families.Count == 0) return Array.Empty<OcrLine>();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var perFamily = new List<(string Tag, List<OcrLine> Lines, double Score, bool Clean)>();
                foreach (var tag in families)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var engine = GetEngine(tag);
                    if (engine == null) continue;

                    var lines = new List<OcrLine>();
                    foreach (var tile in tiles)
                    {
                        lines.AddRange(await RecognizeTileAsync(engine, tag, tile, scale, frame.TimestampMs, region).ConfigureAwait(false));
                    }

                    lines = Dedupe(lines);
                    var (score, clean) = Score(lines, tag);
                    perFamily.Add((tag, lines, score, clean));

                    // Early exit: the preferred/first recognizer produced clean text in its own script.
                    if (!request.ExhaustiveLanguageSearch && clean && !Suspicious(lines) && perFamily.Count == 1 && families.Count > 1) break;
                }

                return Merge(perFamily);
            }
            finally
            {
                _gate.Release();
            }
        }

        // ------------------------------------------------------------------------------------

        private OcrEngine GetEngine(string tag)
        {
            return _engines.GetOrAdd(tag, t =>
            {
                try
                {
                    return OcrEngine.TryCreateFromLanguage(new Language(t));
                }
                catch
                {
                    return null;
                }
            });
        }

        /// <summary>Recognizer tags to try, preferred first, one per script family.</summary>
        internal List<string> CandidateTags(OcrRequest request)
        {
            var installed = InstalledLanguageTags;
            var result = new List<string>();

            foreach (var pref in request.PreferredLanguageTags)
            {
                var match = installed.FirstOrDefault(t => string.Equals(t, pref, StringComparison.OrdinalIgnoreCase))
                            ?? installed.FirstOrDefault(t => Short(t) == Short(pref));
                if (match != null && !result.Contains(match)) result.Add(match);
            }

            // Explicit source language (not a region hint): use only that recognizer.
            if (request.PreferredLanguageTags.Count > 0 && result.Count > 0 && !request.ExhaustiveLanguageSearch && IsExplicit(request))
                return result;

            var byFamily = new Dictionary<string, string>();
            foreach (var tag in installed)
            {
                var fam = Family(tag);
                if (!byFamily.ContainsKey(fam) || IsCanonical(tag, fam)) byFamily[fam] = tag;
            }

            // Latin first (most desktop text; the early exit keeps English-only areas to one pass),
            // then the other scripts. Areas known to hold e.g. Japanese get that hint first instead.
            foreach (var tag in byFamily.OrderBy(kv => kv.Key == "latin" ? 0 : 1).Select(kv => kv.Value))
            {
                if (!result.Any(r => Family(r) == Family(tag))) result.Add(tag);
            }

            return result;
        }

        /// <summary>Hints coming from the watcher are full tags; explicit user choices are bare ISO codes.</summary>
        private static bool IsExplicit(OcrRequest request) => request.PreferredLanguageTags.Count == 1 && !request.PreferredLanguageTags[0].Contains('-');

        private static async Task<List<OcrLine>> RecognizeTileAsync(OcrEngine engine, string tag, CroppedImage tile, double scale, long ts, PixelRect sourceRegion)
        {
            var w = tile.Width;
            var h = tile.Height;
            var pixels = tile.Bgra;
            if (scale > 1.01)
            {
                var nw = (int)Math.Round(w * scale);
                var nh = (int)Math.Round(h * scale);
                pixels = ImageOps.Resize(pixels, w, h, nw, nh);
                w = nw;
                h = nh;
            }

            var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
            var result = await engine.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);

            var lines = new List<OcrLine>(result.Lines.Count);
            var noSpaces = tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double l = double.MaxValue, t = double.MaxValue, r = 0, b = 0;
                foreach (var word in line.Words)
                {
                    var rc = word.BoundingRect;
                    l = Math.Min(l, rc.X);
                    t = Math.Min(t, rc.Y);
                    r = Math.Max(r, rc.X + rc.Width);
                    b = Math.Max(b, rc.Y + rc.Height);
                }

                // Windows OCR inserts spaces between CJK characters; remove those (keep Latin spacing).
                var text = noSpaces ? RemoveCjkSpaces(line.Text) : line.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var box = PixelRect.FromLTRB(
                    tile.DesktopRect.X + (int)Math.Floor(l / scale),
                    tile.DesktopRect.Y + (int)Math.Floor(t / scale),
                    tile.DesktopRect.X + (int)Math.Ceiling(r / scale),
                    tile.DesktopRect.Y + (int)Math.Ceiling(b / scale));
                lines.Add(new OcrLine(text.Trim(), box, ScriptConsistency(text, tag), tag, ts, sourceRegion));
            }

            return lines;
        }

        internal static string RemoveCjkSpaces(string s)
        {
            var chars = s.ToCharArray();
            var sb = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] == ' ' && i > 0 && i < chars.Length - 1 && (IsWide(chars[i - 1]) || IsWide(chars[i + 1]))) continue;
                sb.Append(chars[i]);
            }

            return sb.ToString();

            static bool IsWide(char c) => LanguageId.ScriptOf(c) is Script.Han or Script.Kana or Script.Hangul || (c >= '　' && c <= '〿') || (c >= '＀' && c <= '￯');
        }

        private static List<CroppedImage> SplitForMaxDimension(CroppedImage crop, int max)
        {
            if (crop.Width <= max && crop.Height <= max) return new List<CroppedImage> { crop };
            const int overlap = 96;
            var tiles = new List<CroppedImage>();
            for (var y = 0; y < crop.Height; y += max - overlap)
            {
                for (var x = 0; x < crop.Width; x += max - overlap)
                {
                    var w = Math.Min(max, crop.Width - x);
                    var h = Math.Min(max, crop.Height - y);
                    var buf = new byte[w * h * 4];
                    for (var row = 0; row < h; row++)
                    {
                        Buffer.BlockCopy(crop.Bgra, ((y + row) * crop.Width + x) * 4, buf, row * w * 4, w * 4);
                    }

                    tiles.Add(new CroppedImage(new PixelRect(crop.DesktopRect.X + x, crop.DesktopRect.Y + y, w, h), buf, w, h));
                    if (x + w >= crop.Width) break;
                }

                if (y + Math.Min(max, crop.Height - y) >= crop.Height) break;
            }

            return tiles;
        }

        private static List<OcrLine> Dedupe(List<OcrLine> lines)
        {
            var result = new List<OcrLine>();
            foreach (var l in lines.OrderByDescending(l => l.Text.Length))
            {
                if (result.Any(r => r.Box.IoU(l.Box) > 0.5 || (r.Box.Contains(l.Box) && r.Text.Contains(l.Text, StringComparison.Ordinal)))) continue;
                result.Add(l);
            }

            return result;
        }

        /// <summary>Score = number of characters in the recognizer's own script; clean = mostly its script and not garbled.</summary>
        private static (double Score, bool Clean) Score(List<OcrLine> lines, string tag)
        {
            if (lines.Count == 0) return (0, false);
            var fam = Family(tag);
            int own = 0, letters = 0;
            foreach (var l in lines)
            {
                foreach (var c in l.Text)
                {
                    if (!char.IsLetter(c)) continue;
                    letters++;
                    if (MatchesFamily(c, fam)) own++;
                }
            }

            var filter = new TextFilter();
            var garbled = lines.Count(l => filter.Evaluate(l.Text) is FilterVerdict.Garbled or FilterVerdict.Symbols);
            var clean = letters > 0 && own >= letters * 0.7 && garbled * 3 <= lines.Count;

            if (clean && fam == "latin")
            {
                // A Latin recognizer reading CJK produces a few letters spread over a wide box.
                // Real Latin text has roughly 1.5-2.5 letters per "box height" of width.
                var density = lines.Average(l => l.Text.Count(char.IsLetterOrDigit) / Math.Max(1.0, (double)l.Box.Width / Math.Max(1, l.Box.Height)));
                clean = density >= 0.8;
            }

            return (own, clean);
        }

        /// <summary>Lines a Latin recognizer probably misread (CJK/other script read as junk).</summary>
        private static bool Suspicious(List<OcrLine> lines)
        {
            var filter = new TextFilter();
            return lines.Any(l =>
                filter.Evaluate(l.Text) is FilterVerdict.Garbled or FilterVerdict.Symbols ||
                l.Text.Count(char.IsLetterOrDigit) / Math.Max(1.0, (double)l.Box.Width / Math.Max(1, l.Box.Height)) < 0.8);
        }

        private static double ScriptConsistency(string text, string tag)
        {
            var fam = Family(tag);
            int own = 0, letters = 0;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (MatchesFamily(c, fam)) own++;
            }

            return letters == 0 ? 0 : (double)own / letters;
        }

        /// <summary>Best family wins; lines from other families that hold their own script and don't overlap are merged in.</summary>
        private static IReadOnlyList<OcrLine> Merge(List<(string Tag, List<OcrLine> Lines, double Score, bool Clean)> results)
        {
            if (results.Count == 0) return Array.Empty<OcrLine>();

            // Base = the Latin reading when there is one (Latin recognizers keep word spacing and
            // punctuation best), otherwise the highest-scoring family.
            var baseResult = results.FirstOrDefault(r => Family(r.Tag) == "latin" && r.Lines.Count > 0);
            if (baseResult.Lines == null) baseResult = results.OrderByDescending(r => r.Score).First();
            var merged = new List<OcrLine>(baseResult.Lines);

            // A non-Latin recognizer that finds its own script somewhere means that place holds that
            // script: its reading replaces whatever the base produced there (usually junk letters).
            foreach (var other in results.Where(r => r.Tag != baseResult.Tag).OrderByDescending(r => r.Score))
            {
                if (Family(other.Tag) == "latin") continue;
                foreach (var line in other.Lines)
                {
                    var own = line.Text.Count(c => MatchesFamily(c, Family(other.Tag)));
                    var consistency = ScriptConsistency(line.Text, other.Tag);
                    if (own < 2 || !(consistency >= 0.5 || (own >= 4 && consistency >= 0.3))) continue;
                    var overlapping = merged.Where(m => m.Box.IoU(line.Box) > 0.15 || m.Box.CoveredBy(line.Box) > 0.4 || line.Box.CoveredBy(m.Box) > 0.4).ToList();
                    // Keep a stronger non-Latin reading already chosen for the same place.
                    if (overlapping.Any(m => m.LanguageTag != baseResult.Tag && m.Text.Count(c => MatchesFamily(c, Family(m.LanguageTag ?? ""))) >= own)) continue;
                    foreach (var o in overlapping) merged.Remove(o);
                    merged.Add(line);
                }
            }

            return merged.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
        }

        private static string Short(string tag) => LanguageId.Short(tag);

        private static string Family(string tag) => Short(tag) switch
        {
            "ja" => "ja",
            "zh" => "zh",
            "ko" => "ko",
            "ru" or "uk" or "be" or "bg" or "sr" or "mk" => "cyrillic",
            "ar" or "fa" or "ur" => "arabic",
            "el" => "greek",
            "he" => "hebrew",
            "th" => "thai",
            "hi" => "devanagari",
            _ => "latin",
        };

        private static bool IsCanonical(string tag, string family) => family switch
        {
            "latin" => Short(tag) == "en",
            "cyrillic" => Short(tag) == "ru",
            "arabic" => Short(tag) == "ar",
            "zh" => tag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || tag.Equals("zh-Hans-CN", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        private static bool MatchesFamily(char c, string family)
        {
            var s = LanguageId.ScriptOf(c);
            return family switch
            {
                "ja" => s is Script.Kana or Script.Han,
                "zh" => s == Script.Han,
                "ko" => s == Script.Hangul,
                "cyrillic" => s == Script.Cyrillic,
                "arabic" => s == Script.Arabic,
                "greek" => s == Script.Greek,
                "hebrew" => s == Script.Hebrew,
                "thai" => s == Script.Thai,
                "devanagari" => s == Script.Devanagari,
                _ => s == Script.Latin,
            };
        }
    }
}
