using Brainbox.Core.Diagnostics;

namespace Brainbox.Core.Translation;

/// <summary>
/// Circuit breaker for one provider: after a failure the provider is skipped for an exponentially
/// growing back-off (2s, 4s, 8s ... 60s) so a closed LM Studio / dead network doesn't stall the
/// pipeline, and it is retried automatically afterwards.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly Func<long> _clock;
    private int _failures;
    private long _openUntil;

    public CircuitBreaker(Func<long> clockMs, int baseDelayMs = 2000, int maxDelayMs = 60000)
    {
        _clock = clockMs;
        BaseDelayMs = baseDelayMs;
        MaxDelayMs = maxDelayMs;
    }

    public int BaseDelayMs { get; }
    public int MaxDelayMs { get; }
    public int ConsecutiveFailures => _failures;
    public bool IsOpen => _clock() < Interlocked.Read(ref _openUntil);
    public long RetryAtMs => Interlocked.Read(ref _openUntil);
    public string? LastError { get; private set; }

    public void RecordSuccess()
    {
        _failures = 0;
        Interlocked.Exchange(ref _openUntil, 0);
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        _failures++;
        var delay = (long)Math.Min(MaxDelayMs, BaseDelayMs * Math.Pow(2, Math.Min(10, _failures - 1)));
        Interlocked.Exchange(ref _openUntil, _clock() + delay);
        LastError = error;
    }
}

public sealed record TranslationOutcome(IReadOnlyList<string>? Translations, string? ProviderName, string? Error, double ElapsedMs)
{
    public bool Success => Translations != null;
}

public enum ProviderHealth { Healthy, Degraded, Unavailable }

/// <summary>Status of the provider chain for the UI / tray / notifications.</summary>
public sealed record ProviderStatus(ProviderHealth Health, string ActiveProvider, string? Message);

/// <summary>
/// Ordered provider chain with per-provider circuit breakers and timeouts (§20: retry, fallback,
/// keep monitoring). E.g. LM Studio → Ollama → Google. Never throws for provider failures; the
/// caller receives a failed <see cref="TranslationOutcome"/> and simply tries again later.
/// </summary>
public sealed class ResilientTranslator
{
    private readonly List<(ITranslationProvider Provider, CircuitBreaker Breaker)> _chain = new();
    private readonly Func<long> _clock;
    private readonly ILog _log;
    private ProviderStatus _status = new(ProviderHealth.Healthy, "", null);

    public ResilientTranslator(IEnumerable<ITranslationProvider> providers, Func<long> clockMs, ILog? log = null)
    {
        _clock = clockMs;
        _log = log ?? NullLog.Instance;
        foreach (var p in providers) _chain.Add((p, new CircuitBreaker(clockMs)));
    }

    public event Action<ProviderStatus>? StatusChanged;

    public IReadOnlyList<ITranslationProvider> Providers => _chain.Select(c => c.Provider).ToList();
    public ProviderStatus Status => _status;
    public bool HasProviders => _chain.Count > 0;

    /// <summary>True when the first provider that would be used accepts context.</summary>
    public bool PrimarySupportsContext => _chain.FirstOrDefault(c => !c.Breaker.IsOpen).Provider?.SupportsContext ?? false;

    /// <summary>Earliest time any provider becomes available again (for back-off scheduling).</summary>
    public long NextAvailableAtMs => _chain.Count == 0 ? long.MaxValue : _chain.Min(c => c.Breaker.IsOpen ? c.Breaker.RetryAtMs : 0);

    public async Task<TranslationOutcome> TranslateAsync(TranslationBatch batch, CancellationToken cancellationToken)
    {
        var start = _clock();
        if (_chain.Count == 0)
        {
            SetStatus(new ProviderStatus(ProviderHealth.Unavailable, "", "No translation engine configured."));
            return new TranslationOutcome(null, null, "No translation engine configured.", 0);
        }

        string? lastError = null;
        var index = 0;
        foreach (var (provider, breaker) in _chain)
        {
            if (breaker.IsOpen)
            {
                lastError ??= breaker.LastError;
                index++;
                continue;
            }

            try
            {
                var effective = provider.SupportsContext ? batch : batch with { Context = Array.Empty<ContextEntry>() };
                var result = await provider.TranslateAsync(effective, cancellationToken).ConfigureAwait(false);
                if (result.Count != batch.Lines.Count)
                    throw new TranslationProviderException($"{provider.Name} returned {result.Count} results for {batch.Lines.Count} lines.");

                var wasFailing = breaker.ConsecutiveFailures > 0;
                breaker.RecordSuccess();
                if (wasFailing) _log.Info($"{provider.Name} recovered.");
                SetStatus(index == 0
                    ? new ProviderStatus(ProviderHealth.Healthy, provider.Name, null)
                    : new ProviderStatus(ProviderHealth.Degraded, provider.Name, $"Using fallback {provider.Name}: {lastError}"));
                return new TranslationOutcome(result, provider.Name, null, _clock() - start);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = ex is TranslationProviderException ? ex.Message : $"{provider.Name}: {ex.Message}";
                breaker.RecordFailure(msg);
                lastError = msg;
                _log.Warn($"Translation provider failed ({breaker.ConsecutiveFailures}x), backing off: {msg}");
            }

            index++;
        }

        SetStatus(new ProviderStatus(ProviderHealth.Unavailable, _chain[0].Provider.Name, lastError ?? "All translation engines are unavailable."));
        return new TranslationOutcome(null, null, lastError, _clock() - start);
    }

    private void SetStatus(ProviderStatus s)
    {
        var changed = s.Health != _status.Health || s.ActiveProvider != _status.ActiveProvider || s.Message != _status.Message;
        _status = s;
        if (changed) StatusChanged?.Invoke(s);
    }
}
