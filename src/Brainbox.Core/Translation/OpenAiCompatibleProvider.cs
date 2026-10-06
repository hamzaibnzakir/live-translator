using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brainbox.Core.Translation;

/// <summary>Vision-capable provider used as the optional OCR fallback (§4).</summary>
public interface IVisionTranslator
{
    /// <summary>Reads all text in a PNG image and returns its translation (plain text, one line per line).</summary>
    Task<string> TranslateImageAsync(byte[] png, string targetLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to any OpenAI-compatible chat-completions server: <b>LM Studio</b>
/// (<c>http://localhost:1234/v1</c>), <b>Ollama</b> (<c>http://localhost:11434/v1</c>), llama.cpp
/// server, vLLM, LocalAI, OpenRouter, OpenAI... The model is never hard-coded: when no model is
/// configured the first model the server reports as loaded is used.
///
/// Lines are translated in one request per batch with a rolling context block so pronouns, names
/// and subtitle continuity come out right (§7). The reply is requested as JSON and parsed
/// defensively (code fences, &lt;think&gt; blocks of reasoning models, numbered lists).
/// </summary>
public sealed partial class OpenAiCompatibleProvider : ITranslationProvider, IVisionTranslator, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private string? _resolvedModel;

    public OpenAiCompatibleProvider(string name, string baseUrl, string? model, string? apiKey = null,
        bool isLocal = true, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        Name = name;
        BaseUrl = NormalizeBaseUrl(baseUrl);
        Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        ApiKey = apiKey;
        IsLocal = isLocal;
        Timeout = timeout ?? TimeSpan.FromSeconds(45);

        if (handler == null)
        {
            var uri = new Uri(BaseUrl);
            handler = new SocketsHttpHandler
            {
                // Local servers must never be routed through a corporate/system proxy.
                UseProxy = !uri.IsLoopback,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(3),
                AutomaticDecompression = DecompressionMethods.All,
            };
            _ownsClient = true;
        }

        _http = new HttpClient(handler, disposeHandler: _ownsClient) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public string Name { get; }
    public bool IsLocal { get; }
    public bool SupportsContext => true;
    public string BaseUrl { get; }
    public string? Model { get; }
    public string? ApiKey { get; }
    public TimeSpan Timeout { get; }
    public double Temperature { get; init; } = 0.2;

    /// <summary>Model actually used (configured, or auto-picked from the server).</summary>
    public string? ActiveModel => Model ?? _resolvedModel;

    /// <summary>
    /// Accepts "http://localhost:1234", "localhost:1234/v1", ".../v1/chat/completions" etc. and
    /// returns the API root (".../v1").
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        var u = (url ?? string.Empty).Trim();
        if (u.Length == 0) throw new ArgumentException("Endpoint URL is empty.", nameof(url));
        if (!u.Contains("://", StringComparison.Ordinal)) u = "http://" + u;
        u = u.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/models" })
        {
            if (u.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) u = u[..^suffix.Length];
        }

        var uri = new Uri(u);
        if (uri.AbsolutePath is "" or "/") u += "/v1";
        return u;
    }

    /// <summary>Models the server offers (GET /models). Works for LM Studio, Ollama and OpenAI.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
        AddAuth(req);
        try
        {
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new TranslationProviderException($"{Name}: listing models failed ({(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(body);
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) list.Add(s);
                }
            }
            else if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s) list.Add(s);
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new TranslationProviderException($"{Name} is not reachable at {BaseUrl}.", ex);
        }
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Lines.Count == 0) return Array.Empty<string>();

        var model = await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
        var (system, user) = BuildPrompt(batch);
        var reply = await ChatAsync(model, system, user, EstimateMaxTokens(batch), cancellationToken).ConfigureAwait(false);

        var parsed = ParseTranslations(reply, batch.Lines.Count);
        if (parsed != null) return parsed;

        if (batch.Lines.Count == 1) return new[] { CleanSingle(reply) };

        // The model did not keep the structure: fall back to one request per line.
        var results = new string[batch.Lines.Count];
        for (var i = 0; i < batch.Lines.Count; i++)
        {
            var single = batch with { Lines = new[] { batch.Lines[i] } };
            var (s, u) = BuildPrompt(single);
            var r = await ChatAsync(model, s, u, EstimateMaxTokens(single), cancellationToken).ConfigureAwait(false);
            results[i] = ParseTranslations(r, 1)?[0] ?? CleanSingle(r);
        }

        return results;
    }

    public async Task<string> TranslateImageAsync(byte[] png, string targetLanguage, CancellationToken cancellationToken)
    {
        var model = await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
        var target = LanguageNames.NameOf(targetLanguage);
        var system = $"You read on-screen text from images and translate it into {target}. Output ONLY the {target} translation, one line per text line, in reading order. If there is no readable text, output nothing.";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = Temperature,
            ["max_tokens"] = 1024,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = $"Translate all visible text into {target}." },
                        new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } },
                    },
                },
            },
        };
        var reply = await PostChatAsync(payload, cancellationToken).ConfigureAwait(false);
        return StripReasoning(reply).Trim();
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // ------------------------------------------------------------------------------------

    private async Task<string> EnsureModelAsync(CancellationToken ct)
    {
        if (Model != null) return Model;
        if (_resolvedModel != null) return _resolvedModel;
        var models = await ListModelsAsync(ct).ConfigureAwait(false);
        // Skip embedding models that LM Studio / Ollama also list.
        _resolvedModel = models.FirstOrDefault(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
                         ?? (models.Count > 0 ? models[0] : null)
                         ?? throw new TranslationProviderException($"{Name}: no model is loaded. Load a model in {Name} or pick one in Settings.");
        return _resolvedModel;
    }

    internal static (string System, string User) BuildPrompt(TranslationBatch batch)
    {
        var target = LanguageNames.NameOf(batch.TargetLanguage);
        var source = batch.SourceLanguageHint is null or "auto" ? "the detected source language" : LanguageNames.NameOf(batch.SourceLanguageHint);
        var n = batch.Lines.Count;

        var system =
$@"You are Brainbox Live Translator, a real-time on-screen translator. Text segments were read by OCR from the user's screen (apps, websites, games, subtitles, chats, menus).
Translate each segment from {source} into natural, fluent {target}.
Rules:
- Use the CONTEXT block only to resolve meaning (who is speaking, pronouns, names, honorifics, sentence continuations across subtitle lines). Never translate or output the context itself.
- Keep names, numbers, product names and UI keys consistent. Keep it concise - it is displayed over the original text.
- If a segment is already {target}, or is a name/code that should not change, return it unchanged.
- Fix obvious OCR mistakes silently.
- Reply with ONLY this JSON and nothing else: {{""translations"": [ ...exactly {n} strings, in order... ]}}";

        var sb = new StringBuilder();
        if (batch.Context.Count > 0)
        {
            sb.AppendLine("CONTEXT (earlier and nearby on-screen text, do NOT translate):");
            foreach (var c in batch.Context)
            {
                sb.Append("- ").Append(c.Source);
                if (!string.IsNullOrEmpty(c.Translation)) sb.Append("  =>  ").Append(c.Translation);
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.Append("SEGMENTS (").Append(n).AppendLine("):");
        for (var i = 0; i < n; i++)
        {
            sb.Append(i + 1).Append(". ").AppendLine(batch.Lines[i].Replace('\n', ' '));
        }

        return (system, sb.ToString());
    }

    private static int EstimateMaxTokens(TranslationBatch batch)
    {
        var chars = batch.Lines.Sum(l => l.Length);
        // CJK -> English roughly 1-3 tokens per source char; add headroom for JSON + reasoning models.
        return Math.Clamp(256 + chars * 4, 256, 4096);
    }

    private async Task<string> ChatAsync(string model, string system, string user, int maxTokens, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = Temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = user },
            },
        };
        return await PostChatAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task<string> PostChatAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
        };
        AddAuth(req);

        string body;
        HttpStatusCode status;
        try
        {
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            status = resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException($"{Name} is not reachable at {BaseUrl} ({ex.Message}).", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationProviderException($"{Name} timed out after {Timeout.TotalSeconds:0}s.", ex);
        }

        if ((int)status >= 400)
        {
            var snippet = body.Length > 300 ? body[..300] : body;
            throw new TranslationProviderException($"{Name} returned HTTP {(int)status}: {snippet}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                throw new TranslationProviderException($"{Name} error: {msg}");
            }

            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.ToString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new TranslationProviderException($"{Name} returned an unexpected response.", ex);
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    /// <summary>Extracts exactly <paramref name="expected"/> translations from a model reply, or null.</summary>
    internal static IReadOnlyList<string>? ParseTranslations(string reply, int expected)
    {
        var text = StripReasoning(reply);
        text = CodeFenceRegex().Replace(text, "$1").Trim();

        // 1) JSON object {"translations":[...]} or a bare JSON array, possibly with chatter around it.
        foreach (var candidate in JsonCandidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                JsonElement arr = default;
                if (root.ValueKind == JsonValueKind.Array) arr = root;
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Array) { arr = p.Value; break; }
                    }
                }

                if (arr.ValueKind == JsonValueKind.Array)
                {
                    var list = arr.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty
                            : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("translation", out var t) ? t.GetString() ?? string.Empty
                            : e.ToString())
                        .Select(s => s.Trim())
                        .ToList();
                    if (list.Count == expected) return list;
                }
            }
            catch (JsonException)
            {
                // try next candidate
            }
        }

        // 2) Numbered list "1. ...".
        var numbered = new SortedDictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            var m = NumberedLineRegex().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var idx) && idx >= 1 && idx <= expected)
                numbered[idx] = m.Groups[2].Value.Trim().Trim('"');
        }

        if (numbered.Count == expected) return numbered.Values.ToList();

        // 3) Plain lines, one per segment.
        if (expected > 1)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == expected) return lines;
        }

        return null;
    }

    private static IEnumerable<string> JsonCandidates(string text)
    {
        var o = text.IndexOf('{', StringComparison.Ordinal);
        var oe = text.LastIndexOf('}');
        if (o >= 0 && oe > o) yield return text[o..(oe + 1)];
        var a = text.IndexOf('[', StringComparison.Ordinal);
        var ae = text.LastIndexOf(']');
        if (a >= 0 && ae > a) yield return text[a..(ae + 1)];
    }

    private static string CleanSingle(string reply)
    {
        var t = CodeFenceRegex().Replace(StripReasoning(reply), "$1").Trim();
        t = NumberedLineRegex().Match(t) is { Success: true } m && !t.Contains('\n', StringComparison.Ordinal) ? m.Groups[2].Value : t;
        return t.Trim().Trim('"').Trim();
    }

    internal static string StripReasoning(string reply) => ThinkRegex().Replace(reply ?? string.Empty, string.Empty);

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkRegex();

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"^\s*(\d+)\s*[\.\):]\s*(.*)$")]
    private static partial Regex NumberedLineRegex();
}
