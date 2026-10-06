using System.Net;
using System.Text;
using System.Text.Json;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Core.Text;
using Brainbox.Core.Translation;

namespace Brainbox.Core.Tests;

/// <summary>Controllable clock for deterministic pipeline tests.</summary>
public sealed class FakeClock
{
    public long Now { get; set; } = 1_000;
    public long Read() => Now;
    public void Advance(long ms) => Now += ms;
}

/// <summary>A "text" painted on the fake desktop: a solid colour block that the fake OCR can read back.</summary>
public sealed record FakeText(string Text, PixelRect Box, string LanguageTag = "ja-JP");

/// <summary>
/// Synthetic desktop: paints every <see cref="FakeText"/> as a uniquely coloured rectangle on a
/// grey background. Optionally paints the overlay items too, simulating an OS that does NOT
/// exclude the overlay from capture (used to prove the recursion guard works).
/// </summary>
public sealed class FakeDesktop : IFrameSource
{
    private readonly object _gate = new();
    public List<MonitorInfo> Monitors { get; } = new();
    public List<FakeText> Texts { get; } = new();
    public IReadOnlyList<Brainbox.Core.Overlay.OverlayItem> OverlayToLeak { get; set; } = Array.Empty<Brainbox.Core.Overlay.OverlayItem>();
    public bool Blocked { get; set; }
    public bool HonoursCaptureExclusion { get; set; } = true;
    public int CaptureCalls { get; private set; }

    /// <summary>Colour → text registry shared with <see cref="FakeOcr"/>.</summary>
    public Dictionary<int, (string Text, string Lang)> Palette { get; } = new();

    public FakeDesktop(params MonitorInfo[] monitors)
    {
        Monitors.AddRange(monitors.Length > 0 ? monitors : new[] { new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 640, 480), new PixelRect(0, 0, 640, 440), 1.0, true) });
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        lock (_gate) return Monitors.ToList();
    }

    public DesktopFrame? Capture(PixelRect area)
    {
        lock (_gate)
        {
            CaptureCalls++;
            if (Blocked) return null;
            var stride = area.Width * 4;
            var px = new byte[stride * area.Height];
            for (var i = 0; i < px.Length; i += 4)
            {
                px[i] = 0x40;
                px[i + 1] = 0x40;
                px[i + 2] = 0x40;
                px[i + 3] = 0xFF;
            }

            foreach (var t in Texts) Paint(px, stride, area, t.Box, ColorFor(t.Text, t.LanguageTag));
            if (!HonoursCaptureExclusion)
            {
                foreach (var o in OverlayToLeak) Paint(px, stride, area, o.Box, ColorFor(o.Text, "en-US"));
            }

            return new DesktopFrame(area, px, stride, 0, Monitors.ToList());
        }
    }

    public int ColorFor(string text, string lang)
    {
        foreach (var kv in Palette)
        {
            if (kv.Value.Text == text && kv.Value.Lang == lang) return kv.Key;
        }

        // Distinct, non-grey colours.
        var c = 0x100000 + Palette.Count * 0x0A0B07 % 0xEFFFFF;
        while (Palette.ContainsKey(c) || c == 0x404040) c += 0x010203;
        Palette[c] = (text, lang);
        return c;
    }

    private static void Paint(byte[] px, int stride, PixelRect area, PixelRect box, int rgb)
    {
        var r = box.Intersect(area);
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var o = (y - area.Y) * stride + (x - area.X) * 4;
                px[o] = (byte)(rgb & 0xFF);
                px[o + 1] = (byte)((rgb >> 8) & 0xFF);
                px[o + 2] = (byte)((rgb >> 16) & 0xFF);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>"Reads" the fake desktop: finds palette colours inside the region and returns their text + bounding box.</summary>
public sealed class FakeOcr : IOcrEngine
{
    private readonly FakeDesktop _desktop;
    public FakeOcr(FakeDesktop desktop) => _desktop = desktop;
    public string Name => "FakeOCR";
    public bool IsAvailable => true;
    public IReadOnlyList<string> InstalledLanguageTags => new[] { "en-US", "ja-JP", "fr-FR" };
    public int Calls { get; private set; }
    public bool Throw { get; set; }

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(DesktopFrame frame, PixelRect region, OcrRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("OCR engine exploded");
        var r = region.Intersect(frame.Bounds);
        var boxes = new Dictionary<int, (int L, int T, int R, int B)>();
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var o = (y - frame.Bounds.Y) * frame.Stride + (x - frame.Bounds.X) * 4;
                var rgb = (frame.Bgra[o + 2] << 16) | (frame.Bgra[o + 1] << 8) | frame.Bgra[o];
                if (!_desktop.Palette.ContainsKey(rgb)) continue;
                boxes[rgb] = boxes.TryGetValue(rgb, out var b)
                    ? (Math.Min(b.L, x), Math.Min(b.T, y), Math.Max(b.R, x + 1), Math.Max(b.B, y + 1))
                    : (x, y, x + 1, y + 1);
            }
        }

        IReadOnlyList<OcrLine> lines = boxes.Select(kv =>
        {
            var (text, lang) = _desktop.Palette[kv.Key];
            return new OcrLine(text, PixelRect.FromLTRB(kv.Value.L, kv.Value.T, kv.Value.R, kv.Value.B), 0.9, lang, 0, region);
        }).ToList();
        return Task.FromResult(lines);
    }
}

/// <summary>Dictionary-backed provider that records every request.</summary>
public sealed class FakeProvider : ITranslationProvider
{
    private readonly Dictionary<string, string> _dict;
    public FakeProvider(string name, Dictionary<string, string>? dict = null, bool supportsContext = true)
    {
        Name = name;
        _dict = dict ?? new Dictionary<string, string>();
        SupportsContext = supportsContext;
    }

    public string Name { get; }
    public bool IsLocal => true;
    public bool SupportsContext { get; }
    public bool Down { get; set; }
    public List<TranslationBatch> Requests { get; } = new();
    public int LinesRequested => Requests.Sum(r => r.Lines.Count);

    public Task<IReadOnlyList<string>> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(batch);
        if (Down) throw new TranslationProviderException($"{Name} is not reachable (fake).");
        IReadOnlyList<string> result = batch.Lines.Select(l => _dict.TryGetValue(l, out var t) ? t : "EN:" + l).ToList();
        return Task.FromResult(result);
    }
}

/// <summary>HttpMessageHandler that emulates an OpenAI-compatible server (LM Studio / Ollama).</summary>
public sealed class FakeOpenAiServer : HttpMessageHandler
{
    public List<string> Models { get; } = new() { "text-embedding-nomic", "qwen2.5-7b-instruct" };
    public Func<string, string>? ReplyFor { get; set; }
    public List<JsonDocument> ChatRequests { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool Refuse { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Refuse) throw new HttpRequestException("Connection refused (fake)");
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/models", StringComparison.Ordinal))
        {
            var json = JsonSerializer.Serialize(new { data = Models.Select(m => new { id = m }) });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(body);
        lock (ChatRequests) ChatRequests.Add(doc);
        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":{\"message\":\"boom\"}}") };

        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
        var content = ReplyFor?.Invoke(user) ?? "{\"translations\":[\"Hello\"]}";
        var resp = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resp, Encoding.UTF8, "application/json") };
    }
}
