namespace Brainbox.Core.Translation;

/// <summary>
/// Rolling context buffer (§7). Keeps the last N translated segments (most recent last) and
/// combines them with nearby text from the current screen so the model sees "previous text +
/// current text + nearby text" — essential for subtitles, game dialogue and languages that drop
/// subjects (Japanese, Korean, Chinese).
/// </summary>
public sealed class ContextManager
{
    private readonly object _gate = new();
    private readonly LinkedList<ContextEntry> _history = new();
    private int _capacity;

    public ContextManager(int capacity = 6)
    {
        _capacity = Math.Max(0, capacity);
    }

    /// <summary>Number of earlier segments kept (0 disables context).</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            lock (_gate)
            {
                _capacity = Math.Max(0, value);
                while (_history.Count > _capacity) _history.RemoveFirst();
            }
        }
    }

    /// <summary>Hard cap on characters of context per request (keeps local models fast).</summary>
    public int MaxContextChars { get; set; } = 600;

    public void Record(string source, string translation)
    {
        if (_capacity == 0 || string.IsNullOrWhiteSpace(source)) return;
        lock (_gate)
        {
            // Don't repeat the same line (static UI text is re-seen constantly).
            for (var node = _history.First; node != null; node = node.Next)
            {
                if (node.Value.Source == source)
                {
                    _history.Remove(node);
                    break;
                }
            }

            _history.AddLast(new ContextEntry(source, translation));
            while (_history.Count > _capacity) _history.RemoveFirst();
        }
    }

    public void Clear()
    {
        lock (_gate) _history.Clear();
    }

    /// <summary>
    /// Builds the context block for a batch: recent history first (oldest → newest), then nearby
    /// on-screen lines, excluding the lines being translated, trimmed to <see cref="MaxContextChars"/>.
    /// </summary>
    public IReadOnlyList<ContextEntry> Build(IReadOnlyCollection<string> linesToTranslate, IEnumerable<string> nearbyLines)
    {
        if (_capacity == 0) return Array.Empty<ContextEntry>();

        var exclude = new HashSet<string>(linesToTranslate, StringComparer.Ordinal);
        var result = new List<ContextEntry>();
        List<ContextEntry> history;
        lock (_gate) history = _history.ToList();

        foreach (var h in history)
        {
            if (!exclude.Contains(h.Source)) result.Add(h);
        }

        foreach (var n in nearbyLines)
        {
            if (exclude.Contains(n) || result.Any(r => r.Source == n)) continue;
            result.Add(new ContextEntry(n, null));
        }

        // Trim from the oldest end until within budget (newest context is the most useful).
        var total = result.Sum(r => r.Source.Length + (r.Translation?.Length ?? 0));
        while (result.Count > 0 && total > MaxContextChars)
        {
            total -= result[0].Source.Length + (result[0].Translation?.Length ?? 0);
            result.RemoveAt(0);
        }

        return result;
    }
}
