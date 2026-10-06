using static Brainbox.Core.Caching.SqliteNative;

namespace Brainbox.Core.Caching;

/// <summary>SQLite-backed persistent translation cache (WAL mode, single connection, thread-safe).</summary>
public sealed class SqliteTranslationStore : ITranslationStore
{
    private readonly object _gate = new();
    private readonly long _maxEntries;
    private IntPtr _db;
    private IntPtr _get;
    private IntPtr _put;
    private IntPtr _count;
    private int _writesSincePrune;

    public SqliteTranslationStore(string path, long maxEntries = 200_000)
    {
        _maxEntries = maxEntries;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        EnsureLoaded();
        _db = Open(path);
        _ = sqlite3_busy_timeout(_db, 2000);
        Exec(_db, "PRAGMA journal_mode=WAL;");
        Exec(_db, "PRAGMA synchronous=NORMAL;");
        Exec(_db, @"CREATE TABLE IF NOT EXISTS translations(
                        key TEXT PRIMARY KEY,
                        target TEXT NOT NULL,
                        source TEXT NOT NULL,
                        translation TEXT NOT NULL,
                        provider TEXT NOT NULL,
                        created INTEGER NOT NULL,
                        last_used INTEGER NOT NULL);");
        Exec(_db, "CREATE INDEX IF NOT EXISTS ix_translations_last_used ON translations(last_used);");
        _get = Prepare(_db, "SELECT source, translation, provider FROM translations WHERE key = ?1;");
        _put = Prepare(_db, @"INSERT INTO translations(key, target, source, translation, provider, created, last_used)
                              VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?6)
                              ON CONFLICT(key) DO UPDATE SET translation = excluded.translation,
                                  provider = excluded.provider, last_used = excluded.last_used;");
        _count = Prepare(_db, "SELECT COUNT(*) FROM translations;");
    }

    public long Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _ = sqlite3_reset(_count);
                return sqlite3_step(_count) == SQLITE_ROW ? sqlite3_column_int64(_count, 0) : 0;
            }
        }
    }

    public bool TryGet(string key, out CachedTranslation value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = sqlite3_reset(_get);
            _ = sqlite3_clear_bindings(_get);
            BindText(_get, 1, key);
            if (sqlite3_step(_get) == SQLITE_ROW)
            {
                value = new CachedTranslation(ColumnText(_get, 0), ColumnText(_get, 1), ColumnText(_get, 2));
                _ = sqlite3_reset(_get);
                return true;
            }

            _ = sqlite3_reset(_get);
            value = null!;
            return false;
        }
    }

    public void Put(string key, string target, CachedTranslation value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = sqlite3_reset(_put);
            _ = sqlite3_clear_bindings(_put);
            BindText(_put, 1, key);
            BindText(_put, 2, target);
            BindText(_put, 3, value.Source);
            BindText(_put, 4, value.Translation);
            BindText(_put, 5, value.Provider);
            _ = sqlite3_bind_int64(_put, 6, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var rc = sqlite3_step(_put);
            _ = sqlite3_reset(_put);
            if (rc != SQLITE_DONE) throw new InvalidOperationException($"sqlite insert failed ({rc}): {ErrorMessage(_db)}");

            if (++_writesSincePrune >= 500)
            {
                _writesSincePrune = 0;
                Exec(_db, $"DELETE FROM translations WHERE key IN (SELECT key FROM translations ORDER BY last_used DESC LIMIT -1 OFFSET {_maxEntries});");
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Exec(_db, "DELETE FROM translations;");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            _ = sqlite3_finalize(_get);
            _ = sqlite3_finalize(_put);
            _ = sqlite3_finalize(_count);
            _get = _put = _count = IntPtr.Zero;
            _ = sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_db == IntPtr.Zero, this);
}
