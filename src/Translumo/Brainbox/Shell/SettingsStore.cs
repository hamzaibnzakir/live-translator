using System;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Settings;

namespace Brainbox.Desktop.Shell
{
    /// <summary>Well-known locations under %LOCALAPPDATA%\BrainboxLiveTranslator.</summary>
    public static class BrainboxPaths
    {
        public static string Root
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("BRAINBOX_DATA_DIR");
                var dir = string.IsNullOrWhiteSpace(overrideDir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainboxLiveTranslator")
                    : overrideDir;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string Settings => Path.Combine(Root, "settings.json");
        public static string Cache => Path.Combine(Root, "translation-cache.db");
        public static string Logs => Path.Combine(Root, "logs");
    }

    /// <summary>
    /// Loads and auto-saves <see cref="BrainboxSettings"/> (debounced). The custom endpoint API key
    /// is encrypted at rest with Windows DPAPI (current user).
    /// </summary>
    public sealed class SettingsStore : IDisposable
    {
        private const string Prefix = "dpapi:";
        private readonly string _path;
        private readonly ILog _log;
        private readonly Timer _debounce;

        public SettingsStore(string path, ILog log)
        {
            _path = path;
            _log = log;
            Settings = BrainboxSettings.Load(path);
            if (Settings.CustomApiKey.StartsWith(Prefix, StringComparison.Ordinal)) Settings.CustomApiKey = Unprotect(Settings.CustomApiKey);
            _debounce = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            Settings.PropertyChanged += OnChanged;
        }

        public BrainboxSettings Settings { get; }

        private void OnChanged(object sender, PropertyChangedEventArgs e) => _debounce.Change(400, Timeout.Infinite);

        public void SaveNow()
        {
            try
            {
                var copy = Settings.Clone();
                if (!string.IsNullOrEmpty(copy.CustomApiKey)) copy.CustomApiKey = Protect(copy.CustomApiKey);
                copy.Save(_path);
            }
            catch (Exception ex)
            {
                _log.Warn("Saving settings failed", ex);
            }
        }

        private static string Protect(string plain)
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }

        private string Unprotect(string stored)
        {
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not decrypt the stored API key (different Windows user?)", ex);
                return "";
            }
        }

        public void Dispose()
        {
            Settings.PropertyChanged -= OnChanged;
            _debounce.Dispose();
            SaveNow();
        }
    }
}
