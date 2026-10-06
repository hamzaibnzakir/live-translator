namespace Brainbox.Core.Translation;

/// <summary>One segment of earlier or surrounding text sent along as context (never translated itself).</summary>
public sealed record ContextEntry(string Source, string? Translation);

/// <summary>A batch of lines to translate together (lines from the same screen area share context).</summary>
public sealed record TranslationBatch(
    IReadOnlyList<string> Lines,
    string TargetLanguage,
    string? SourceLanguageHint,
    IReadOnlyList<ContextEntry> Context);

/// <summary>A translation backend (local LLM, Ollama, LM Studio, cloud translator...).</summary>
public interface ITranslationProvider
{
    string Name { get; }

    /// <summary>Runs on this machine (no data leaves the PC).</summary>
    bool IsLocal { get; }

    /// <summary>Whether the provider makes use of <see cref="TranslationBatch.Context"/>.</summary>
    bool SupportsContext { get; }

    /// <summary>Translates every line; the result has exactly <c>batch.Lines.Count</c> entries.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken);
}

/// <summary>Thrown when a provider is unreachable or returned something unusable.</summary>
public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(string message) : base(message) { }
    public TranslationProviderException(string message, Exception inner) : base(message, inner) { }
    public TranslationProviderException() { }
}

/// <summary>Per-line adapter so any single-string translator can join the provider chain.</summary>
public sealed class DelegateTranslationProvider : ITranslationProvider
{
    private readonly Func<string, string, string?, CancellationToken, Task<string>> _translateLine;
    private readonly int _maxParallel;

    /// <param name="translateLine">(text, targetIso, sourceIsoOrNull, ct) =&gt; translation.</param>
    public DelegateTranslationProvider(string name, bool isLocal, Func<string, string, string?, CancellationToken, Task<string>> translateLine, int maxParallel = 3)
    {
        Name = name;
        IsLocal = isLocal;
        _translateLine = translateLine;
        _maxParallel = Math.Max(1, maxParallel);
    }

    public string Name { get; }
    public bool IsLocal { get; }
    public bool SupportsContext => false;

    public async Task<IReadOnlyList<string>> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken)
    {
        var results = new string[batch.Lines.Count];
        using var gate = new SemaphoreSlim(_maxParallel);
        var tasks = batch.Lines.Select(async (line, i) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[i] = await _translateLine(line, batch.TargetLanguage, batch.SourceLanguageHint, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
