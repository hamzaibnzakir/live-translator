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

        if (batch.Lines.Count > 1)
        {
            var joined = await TranslateOneAsync(string.Join("\n", batch.Lines.Select(l => l.Replace('\n', ' '))), sl, tl, cancellationToken).ConfigureAwait(false);
            var parts = joined.Split('\n').Select(p => p.Trim()).ToList();
            if (parts.Count == batch.Lines.Count) return parts;
        }

        var results = new string[batch.Lines.Count];
        for (var i = 0; i < batch.Lines.Count; i++)
        {
            results[i] = await TranslateOneAsync(batch.Lines[i], sl, tl, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<string> TranslateOneAsync(string text, string sl, string tl, CancellationToken ct)
    {
        Exception? first = null;
        try
        {
            var url = GtxUrl(sl, tl, Uri.EscapeDataString(text));
            var body = await GetAsync(url, ct).ConfigureAwait(false);
            var parsed = ParseGtx(body);
            if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            first = new TranslationProviderException("Google returned an empty translation.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            first = ex;
        }

        try
        {
            var url = MobileUrl(sl, tl, Uri.EscapeDataString(text));
            var body = await GetAsync(url, ct).ConfigureAwait(false);
            var m = ResultContainerRegex().Match(body);
            if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new TranslationProviderException($"Google Translate is not reachable ({first?.Message ?? ex.Message}).", ex);
        }

        throw new TranslationProviderException($"Google Translate returned an unexpected response ({first?.Message}).", first ?? new InvalidOperationException());
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

    public void Dispose()
    {
        _http.Dispose();
    }

    [GeneratedRegex(@"class=""result-container"">(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex ResultContainerRegex();
}
