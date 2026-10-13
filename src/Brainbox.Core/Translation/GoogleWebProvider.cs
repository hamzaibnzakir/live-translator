using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brainbox.Core.Translation;

/// <summary>
/// Google Translate without an API key. Uses the public "gtx" JSON endpoint
/// (translate.googleapis.com) and falls back to the mobile web page if that ever fails.
/// Lines of one batch go out in a single request (newline-joined) to keep it fast.
/// </summary>
public sealed partial class GoogleWebProvider : ITranslationProvider, IDisposable
{
    private static string GtxUrl(string sl, string tl, string q) =>
        $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={q}";

    private static string DictUrl(string sl, string tl, string q) =>
        $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={sl}&tl={tl}&q={q}";

    private static string MobileUrl(string sl, string tl, string q) =>
        $"https://translate.google.com/m?sl={sl}&tl={tl}&hl={tl}&q={q}";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public GoogleWebProvider(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _ownsClient = handler == null;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }, disposeHandler: _ownsClient)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    public string Name => "Google Translate";
    public bool IsLocal => false;
    public bool SupportsContext => false;

    public static string GoogleCode(string? iso) => iso switch
    {
        null or "" or "auto" => "auto",
        "zh" => "zh-CN",
        "he" => "iw",
        _ => iso,
    };

    public async Task<IReadOnlyList<string>> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Lines.Count == 0) return Array.Empty<string>();
        var sl = GoogleCode(batch.SourceLanguageHint);
        var tl = GoogleCode(batch.TargetLanguage);

        var results = new string[batch.Lines.Count];

        // One request per language group: with "auto" Google detects ONE language per request, so a
        // French line batched with a Japanese line comes back half untranslated.
        var keys = batch.Lines.Select(l => GroupKey(l, sl)).ToList();
        var latinLanguages = keys.Where(k => k.StartsWith("latin:", StringComparison.Ordinal) && k != "latin:?").Distinct().ToList();
        if (latinLanguages.Count == 1)
        {
            // Short Latin lines ("Au revoir") are often undetermined: they belong with the one Latin language on screen.
            for (var i = 0; i < keys.Count; i++) if (keys[i] == "latin:?") keys[i] = latinLanguages[0];
        }

        var groups = batch.Lines
            .Select((line, index) => (line, index))
            .GroupBy(x => keys[x.index])
            .ToList();

        foreach (var group in groups)
        {
            var items = group.ToList();
            if (items.Count > 1)
            {
                var joined = await TranslateOneAsync(string.Join("\n", items.Select(x => x.line.Replace('\n', ' '))), sl, tl, cancellationToken).ConfigureAwait(false);
                var parts = joined.Split('\n').Select(p => p.Trim()).ToList();
                if (parts.Count == items.Count)
                {
                    for (var i = 0; i < items.Count; i++) results[items[i].index] = parts[i];
                    continue;
                }
            }

            foreach (var (line, index) in items)
            {
                results[index] = await TranslateOneAsync(line, sl, tl, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>Lines that Google's auto-detect will treat as one language.</summary>
    internal static string GroupKey(string line, string sourceLanguage)
    {
        if (sourceLanguage != "auto") return sourceLanguage;
        var guess = Brainbox.Core.Text.LanguageId.Detect(line);
        return guess.Script switch
        {
            Brainbox.Core.Text.Script.Han or Brainbox.Core.Text.Script.Kana => guess.Code == "zh" ? "zh" : "ja",
            Brainbox.Core.Text.Script.Latin or Brainbox.Core.Text.Script.Unknown => guess.IsUndetermined || guess.Code == "en" ? "latin:?" : "latin:" + guess.Code,
            _ => guess.Script.ToString(),
        };
    }

    private async Task<string> TranslateOneAsync(string text, string sl, string tl, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(text);
        var attempts = new (string Url, Func<string, string> Parse)[]
        {
            (GtxUrl(sl, tl, q), ParseGtx),
            (DictUrl(sl, tl, q), ParseDict),
            (MobileUrl(sl, tl, q), ParseMobile),
        };

        Exception? first = null;
        Exception? last = null;
        foreach (var (url, parse) in attempts)
        {
            try
            {
                var parsed = parse(await GetAsync(url, ct).ConfigureAwait(false));
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                last = new TranslationProviderException("empty translation");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
            }

            first ??= last;
        }

        throw new TranslationProviderException($"Google Translate is not reachable ({first?.Message ?? last?.Message}).", last ?? new InvalidOperationException());
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new TranslationProviderException($"HTTP {(int)resp.StatusCode}");
            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationProviderException("timed out", ex);
        }
    }

    /// <summary>Parses [[["translated","source",...],...],...] into the joined translation.</summary>
    internal static string ParseGtx(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }

        return sb.ToString();
    }

    /// <summary>Parses ["text"] or [["text","detected"]] (dict-chrome-ex).</summary>
    internal static string ParseDict(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return string.Empty;
        var e = root[0];
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) e = e[0];
        return e.ValueKind == JsonValueKind.String ? WebUtility.HtmlDecode(e.GetString() ?? string.Empty) : string.Empty;
    }

    internal static string ParseMobile(string body)
    {
        var m = ResultContainerRegex().Match(body);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    [GeneratedRegex(@"class=""result-container"">(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex ResultContainerRegex();
}
