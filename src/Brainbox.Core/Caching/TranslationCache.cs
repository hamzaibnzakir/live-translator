using Brainbox.Core.Diagnostics;
using Brainbox.Core.Text;

namespace Brainbox.Core.Caching;

public sealed record CachedTranslation(string Source, string Translation, string Provider);

/// <summary>Persistent store behind the in-memory cache.</summary>
public interface ITranslationStore : IDisposable
{
    bool TryGet(string key, out CachedTranslation value);
    void Put(string key, string target, CachedTranslation value);
    long Count { get; }
    void Clear();
}

/// <summary>
/// Translation cache (§9): normalize → hash → memory LRU → persistent SQLite store.
/// The same sentence appearing again (even after a restart) never triggers another request.
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private readonly object _gate = new();
    private readonly int _memoryCapacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, CachedTranslation Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, CachedTranslation Value)> _lru = new();
    private readonly ITranslationStore? _store;
    private readonly ILog _log;

    public TranslationCache(ITranslationStore? store = null, int memoryCapacity = 5000, ILog? log = null)
    {
        _store = store;
        _memoryCapacity = Math.Max(16, memoryCapacity);
        _log = log ?? NullLog.Instance;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public int MemoryCount { get { lock (_gate) return _map.Count; } }
    public long PersistentCount => SafeStore(s => s.Count, 0L);
    public bool IsPersistent => _store != null;

    public static string KeyFor(string text, string targetLanguage) =>
        TextNormalizer.CacheKey(TextNormalizer.Normalize(text), targetLanguage);

    public bool TryGet(string text, string targetLanguage, out CachedTranslation value)
    {
        var key = KeyFor(text, targetLanguage);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                Hits++;
                return true;
            }
        }

        if (_store != null && SafeStore(s => s.TryGet(key, out var v) ? v : null, null) is { } stored)
        {
            AddToMemory(key, stored);
            lock (_gate) Hits++;
            value = stored;
            return true;
        }

        lock (_gate) Misses++;
        value = null!;
        return false;
    }

    public void Put(string text, string targetLanguage, string translation, string provider)
    {
        var key = KeyFor(text, targetLanguage);
        var entry = new CachedTranslation(TextNormalizer.Normalize(text), translation, provider);
        AddToMemory(key, entry);
        SafeStore(s =>
        {
            s.Put(key, targetLanguage, entry);
            return true;
        }, false);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            Hits = Misses = 0;
        }

        SafeStore(s =>
        {
            s.Clear();
            return true;
        }, false);
    }

    public void Dispose() => _store?.Dispose();

    private void AddToMemory(string key, CachedTranslation value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = _lru.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > _memoryCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    private T SafeStore<T>(Func<ITranslationStore, T> op, T fallback)
    {
        if (_store == null) return fallback;
        try
        {
            return op(_store);
        }
        catch (Exception ex)
        {
            // A broken disk cache must never stop translation; memory cache keeps working.
            _log.Warn("Translation cache store error", ex);
            return fallback;
        }
    }
}
