using System;
using System.IO;
using Brainbox.Core.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Brainbox.Desktop.Shell
{
    /// <summary>Brainbox log → rolling files in %LOCALAPPDATA%\BrainboxLiveTranslator\logs.</summary>
    public sealed class SerilogLog : ILog, IDisposable
    {
        private readonly LoggingLevelSwitch _level = new(LogEventLevel.Information);
        private readonly Logger _logger;

        public SerilogLog(string directory)
        {
            Directory.CreateDirectory(directory);
            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_level)
                .WriteTo.File(Path.Combine(directory, "brainbox-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    shared: true, flushToDiskInterval: TimeSpan.FromSeconds(2))
                .CreateLogger();
        }

        public bool Enabled { get; set; } = true;

        public bool IsDebugEnabled => Enabled && _level.MinimumLevel <= LogEventLevel.Debug;

        public void SetDebug(bool debug) => _level.MinimumLevel = debug ? LogEventLevel.Debug : LogEventLevel.Information;

        public void Write(Brainbox.Core.Diagnostics.LogLevel level, string message, Exception exception = null)
        {
            // Warnings and errors are always kept, even with logging switched off.
            if (!Enabled && level < Brainbox.Core.Diagnostics.LogLevel.Warn) return;
            var l = level switch
            {
                Brainbox.Core.Diagnostics.LogLevel.Debug => LogEventLevel.Debug,
                Brainbox.Core.Diagnostics.LogLevel.Info => LogEventLevel.Information,
                Brainbox.Core.Diagnostics.LogLevel.Warn => LogEventLevel.Warning,
                _ => LogEventLevel.Error,
            };
            _logger.Write(l, exception, "{Message}", message);
        }

        public void Dispose() => _logger.Dispose();
    }
}
