using System;
using Microsoft.Win32;

namespace Brainbox.Desktop.Shell
{
    /// <summary>"Start Brainbox Translator with Windows" (§14) via the per-user Run key (no admin rights).</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "BrainboxLiveTranslator";
        public const string AutostartArg = "--autostart";

        public static string Command => $"\"{Environment.ProcessPath}\" {AutostartArg}";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates/removes the Run entry. Returns false when the registry is not writable (policy).</summary>
        public static bool Apply(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (enable) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
