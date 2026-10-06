namespace Brainbox.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Minimal logging abstraction so the core has no logging-framework dependency.</summary>
public interface ILog
{
    bool IsDebugEnabled { get; }
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message)
    {
        if (log.IsDebugEnabled) log.Write(LogLevel.Debug, message);
    }

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warn, message, ex);
    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public bool IsDebugEnabled => false;
    public void Write(LogLevel level, string message, Exception? exception = null) { }
}

/// <summary>Thread-safe in-memory log (tests, debug panel).</summary>
public sealed class MemoryLog : ILog
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();
    public bool IsDebugEnabled { get; set; } = true;

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _entries.Add((level, exception == null ? message : message + " | " + exception.Message));
            if (_entries.Count > 2000) _entries.RemoveRange(0, 500);
        }
    }
}
