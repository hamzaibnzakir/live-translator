using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brainbox.Core.Settings;
using Brainbox.Core.Translation;
using Brainbox.Desktop.Capture;
using Brainbox.Desktop.Shell;
using Translumo.OCR.WindowsOCR;

namespace Brainbox.Desktop.UI
{
    public sealed record Option(object Value, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Settings (§19): Translation, Screen, Performance, Overlay, Startup, Hotkeys, Advanced.</summary>
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly Action _openClassic;
        private string _modelStatus = "Leave empty to use the model currently loaded in your local AI server.";

        public SettingsWindow(BrainboxController controller, Action openClassic)
        {
            Controller = controller;
            _openClassic = openClassic;
            InitializeComponent();
            DataContext = this;
            ApiKeyBox.Password = Settings.CustomApiKey ?? "";
            Settings.PropertyChanged += (_, _) => RaiseAll();
            Controller.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BrainboxController.MetricsText)) Raise(nameof(CacheText));
            };
            Loaded += (_, _) => OnRefreshModels(null, null);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public BrainboxController Controller { get; }
        public BrainboxSettings Settings => Controller.Settings;

        public Option[] SourceLanguages { get; } =
            new[] { new Option("auto", "Automatic (detect)") }.Concat(LanguageNames.All.Where(l => l.Code != "en").Select(l => new Option(l.Code, l.Name))).Concat(new[] { new Option("en", "English") }).ToArray();

        public Option[] TargetLanguages { get; } = LanguageNames.All.Select(l => new Option(l.Code, l.Name)).ToArray();

        public Option[] Engines { get; } = Enum.GetValues(typeof(TranslationEngineKind)).Cast<TranslationEngineKind>().Select(k => new Option(k, EngineFactory.DisplayName(k))).ToArray();

        public Option[] Scopes { get; } =
        {
            new(ScreenScope.AllMonitors, "All monitors"),
            new(ScreenScope.CurrentMonitor, "Current monitor (where the active window is)"),
            new(ScreenScope.SpecificMonitor, "Specific monitor"),
        };

        public Option[] Monitors => GdiFrameSource.QueryMonitors()
            .Select((m, i) => new Option(m.DeviceName, $"{i + 1}. {m.DeviceName.Replace(@"\\.\", "")} — {m.Bounds.Width}×{m.Bounds.Height} at ({m.Bounds.X},{m.Bounds.Y}), {m.DpiScale * 100:0}%{(m.IsPortrait ? ", portrait" : "")}{(m.IsPrimary ? ", primary" : "")}"))
            .ToArray();

        public Option[] Modes { get; } =
        {
            new(PerformanceMode.Balanced, "Balanced"),
            new(PerformanceMode.Performance, "Performance"),
            new(PerformanceMode.BatterySaver, "Battery Saver"),
            new(PerformanceMode.MaximumAccuracy, "Maximum Accuracy"),
        };

        public Option[] OcrEngines { get; } = { new("Windows OCR", "Windows OCR (built in, local)") };

        public Option[] CaptureBackends { get; } =
        {
            new(CaptureBackend.Auto, "Automatic"),
            new(CaptureBackend.Gdi, "Compatibility (CPU, GDI)"),
            new(CaptureBackend.DesktopDuplication, "Desktop Duplication (GPU)"),
        };

        public Option[] Styles { get; } =
        {
            new(OverlayStyle.Replace, "Replace — cover the original text"),
            new(OverlayStyle.Subtitle, "Floating subtitle — below the text"),
            new(OverlayStyle.Bubble, "Compact bubble — above the text"),
        };

        public Option[] Backgrounds { get; } =
        {
            new(OverlayBackground.Auto, "Auto — match the original colours"),
            new(OverlayBackground.Dark, "Dark"),
            new(OverlayBackground.Light, "Light"),
            new(OverlayBackground.Blur, "Frosted (semi-transparent)"),
        };

        public string[] Fonts { get; } = Fonts_();

        private static string[] Fonts_() =>
            System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToArray();

        public Option[] InstallableOcrLanguages => WindowsOCRHelper.GetAllLanguageOCRCapabilities()
            .Where(c => !WindowsOCRHelper.IsLanguageOcrCapabilityInstalled(c.LanguageTag, true))
            .Select(c => new Option(c.NameTag, $"{c.NameTag} — {LanguageNames.NameOf(c.ShortTag)}"))
            .ToArray();

        public ObservableCollection<string> Models { get; } = new();

        public bool IsLmStudio => Settings.Engine == TranslationEngineKind.LmStudio;
        public bool IsOllama => Settings.Engine == TranslationEngineKind.Ollama;
        public bool IsCustom => Settings.Engine == TranslationEngineKind.CustomOpenAi;
        public bool IsTranslumo => Settings.Engine == TranslationEngineKind.Translumo;
        public bool IsAiEngine => IsLmStudio || IsOllama || IsCustom;
        public bool IsSpecificMonitor => Settings.ScreenScope == ScreenScope.SpecificMonitor;

        public string ModelStatus
        {
            get => _modelStatus;
            set
            {
                _modelStatus = value;
                Raise();
            }
        }

        public string ContextSizeText => Settings.ContextSize == 0 ? "Context: off" : $"Context: last {Settings.ContextSize} segments";
        public string IntervalText => Settings.DetectionIntervalMs == 0 ? $"Detection interval: automatic ({PerformanceProfile.From(Settings).IntervalMs} ms)" : $"Detection interval: {Settings.DetectionIntervalMs} ms";
        public string SensitivityText => $"Change detection sensitivity: {Settings.Sensitivity}/10";
        public string OpacityText => $"Opacity: {Settings.OverlayOpacity * 100:0}%";
        public string FontSizeText => Settings.FontSize == 0 ? "Font size: match original text" : $"Font size: {Settings.FontSize:0} pt";
        public string GlowText => $"Glow intensity: {Settings.GlowIntensity * 100:0}%";

        public string ModeDescription => Settings.PerformanceMode switch
        {
            PerformanceMode.Performance => "Fastest detection (~4 checks/s). Uses more CPU.",
            PerformanceMode.BatterySaver => "Checks every 1.5 s and backs off on busy areas. Lowest power use.",
            PerformanceMode.MaximumAccuracy => "Upscaled OCR, tries every installed script, periodic full re-scan, optional vision fallback.",
            _ => "Twice per second, OCR only on changed areas. Recommended.",
        };

        public string OcrLanguagesText
        {
            get
            {
                var tags = Controller.Ocr?.InstalledLanguageTags ?? Array.Empty<string>();
                return tags.Count == 0
                    ? "No OCR languages installed! Install at least one below (e.g. ja-JP for Japanese)."
                    : "Installed OCR languages: " + string.Join(", ", tags) + ". Install the language you want to read (e.g. ja-JP, zh-CN, ko-KR).";
            }
        }

        public string CacheText => Controller.Cache == null ? "" :
            $"{Controller.Cache.PersistentCount:N0} saved translations · {Controller.Cache.Hits:N0} hits this session" + (Controller.Cache.IsPersistent ? "" : " (memory only)");

        public string StartupStatus => StartupManager.IsEnabled() ? "Windows startup entry is registered." : "Not registered with Windows startup.";

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded && sender != NavTranslation) return;
            var target = (string)((RadioButton)sender).Tag;
            foreach (var name in new[] { "PageTranslation", "PageScreen", "PagePerformance", "PageOverlay", "PageStartup", "PageHotkeys", "PageAdvanced" })
            {
                if (FindName(name) is UIElement page) page.Visibility = name == target ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void OnRefreshModels(object sender, RoutedEventArgs e)
        {
            if (!IsAiEngine) return;
            ModelStatus = "Looking for models…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var models = await EngineFactory.ListModelsAsync(Settings, cts.Token);
                Models.Clear();
                foreach (var m in models) Models.Add(m);
                ModelStatus = models.Count == 0
                    ? "Server reachable but no model is loaded — load one in LM Studio / pull one in Ollama."
                    : $"{models.Count} model(s) available. Leave empty to use the loaded one automatically.";
            }
            catch (Exception ex)
            {
                ModelStatus = "Not reachable: " + ex.Message + " — start the local server, then press Refresh.";
            }
        }

        private void OnApiKeyChanged(object sender, RoutedEventArgs e) => Settings.CustomApiKey = ApiKeyBox.Password;

        private async void OnInstallOcr(object sender, RoutedEventArgs e)
        {
            if (OcrInstallCombo.SelectedValue is not string tag) return;
            var ok = await WindowsOCRHelper.InstallOcrLanguageCapability(tag);
            MessageBox.Show(this, ok ? $"{tag} OCR installed. Restart Brainbox to use it." : $"Installing {tag} failed or was cancelled.", "Brainbox", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Raise(nameof(InstallableOcrLanguages));
        }

        private void OnClearCache(object sender, RoutedEventArgs e)
        {
            Controller.Cache?.Clear();
            Raise(nameof(CacheText));
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", BrainboxPaths.Logs) { UseShellExecute = true });
        }

        private void OnClassicSettings(object sender, RoutedEventArgs e) => _openClassic();

        private void OnRunSelfTest(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--selftest --interactive") { UseShellExecute = false });
        }

        private void RaiseAll()
        {
            foreach (var p in new[]
                     {
                         nameof(IsLmStudio), nameof(IsOllama), nameof(IsCustom), nameof(IsTranslumo), nameof(IsAiEngine), nameof(IsSpecificMonitor),
                         nameof(ContextSizeText), nameof(IntervalText), nameof(SensitivityText), nameof(OpacityText), nameof(FontSizeText), nameof(GlowText),
                         nameof(ModeDescription), nameof(StartupStatus),
                     })
            {
                Raise(p);
            }
        }

        private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
