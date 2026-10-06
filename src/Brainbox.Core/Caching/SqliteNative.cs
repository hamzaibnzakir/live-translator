using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Brainbox.Core.Caching;

/// <summary>
/// Minimal SQLite P/Invoke layer. On Windows it binds to <c>winsqlite3.dll</c>, the SQLite build
/// that ships inside Windows 10/11 (System32) — so the persistent cache needs no extra native
/// package. On Linux/macOS (tests, CI) it binds to the system libsqlite3.
/// </summary>
internal static unsafe class SqliteNative
{
    private const string Lib = "brainbox_sqlite3";

    public const int SQLITE_OK = 0;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;
    public const int SQLITE_OPEN_READWRITE = 0x2;
    public const int SQLITE_OPEN_CREATE = 0x4;
    public const int SQLITE_OPEN_FULLMUTEX = 0x10000;
    private static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    static SqliteNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    /// <summary>Forces the static constructor (resolver registration) to run.</summary>
    public static void EnsureLoaded() => _ = sqlite3_libversion_number();

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "winsqlite3.dll", "sqlite3.dll", "e_sqlite3.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so", "libe_sqlite3.so" };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, assembly, DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories, out var h)) return h;
            if (NativeLibrary.TryLoad(c, out h)) return h;
        }

        return IntPtr.Zero;
    }

    [DllImport(Lib)] public static extern int sqlite3_libversion_number();
    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte* filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nBytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte* text, int nBytes, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);

    public static string ErrorMessage(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "unknown sqlite error";

    public static int BindText(IntPtr stmt, int index, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        fixed (byte* p = bytes)
        {
            // SQLITE_TRANSIENT: SQLite copies the buffer before we unpin it.
            return sqlite3_bind_text(stmt, index, p, bytes.Length, SQLITE_TRANSIENT);
        }
    }

    public static string ColumnText(IntPtr stmt, int col)
    {
        var ptr = sqlite3_column_text(stmt, col);
        if (ptr == IntPtr.Zero) return string.Empty;
        var len = sqlite3_column_bytes(stmt, col);
        return Encoding.UTF8.GetString((byte*)ptr, len);
    }

    public static IntPtr Prepare(IntPtr db, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql + "\0");
        fixed (byte* p = bytes)
        {
            var rc = sqlite3_prepare_v2(db, p, -1, out var stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException($"sqlite prepare failed ({rc}): {ErrorMessage(db)} — {sql}");
            return stmt;
        }
    }

    public static void Exec(IntPtr db, string sql)
    {
        var stmt = Prepare(db, sql);
        try
        {
            int rc;
            do
            {
                rc = sqlite3_step(stmt);
            } while (rc == SQLITE_ROW);

            if (rc != SQLITE_DONE) throw new InvalidOperationException($"sqlite exec failed ({rc}): {ErrorMessage(db)} — {sql}");
        }
        finally
        {
            _ = sqlite3_finalize(stmt);
        }
    }

    public static IntPtr Open(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = bytes)
        {
            var rc = sqlite3_open_v2(p, out var db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                var msg = db != IntPtr.Zero ? ErrorMessage(db) : "open failed";
                if (db != IntPtr.Zero) _ = sqlite3_close_v2(db);
                throw new InvalidOperationException($"sqlite open failed ({rc}): {msg}");
            }

            return db;
        }
    }
}
