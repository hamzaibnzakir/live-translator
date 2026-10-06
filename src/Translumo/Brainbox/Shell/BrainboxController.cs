using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Brainbox.Core.Caching;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using Brainbox.Core.Pipeline;
using Brainbox.Core.Settings;
using Brainbox.Core.Translation;
using Brainbox.Desktop.Capture;
using Brainbox.Desktop.Interop;
using Brainbox.Desktop.Ocr;
using Brainbox.Desktop.Overlay;
using Microsoft.Win32;

namespace Brainbox.Desktop.Shell
{
    /// <summary>
    /// Wires the engine (Brainbox.Core) to Windows: capture, OCR, overlay, glow, virtual-desktop
    /// keeper, hotkeys, startup, and exposes bindable state for the tray and the UI.
    /// </summary>
    public sealed class BrainboxController : INotifyPropertyChanged, IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly List<IDisposable> _engineOwned = new();
        private readonly Func<ITranslationProvider> _translumoProvider;
        private SettingsStore _store;
        private TranslationCache _cache;
        private SwitchableFrameSource _frames;
        private WindowsOcrEngineAdapter _ocr;
        private OverlayManager _overlay;
        private VirtualDesktopKeeper _keeper;
        private GlobalHotkeys _hotkeys;
        private DispatcherTimer _uiTimer;
        private WatcherStatus _status = new(WatcherState.Stopped, "Starting…", null, null);
        private string _hotkeyErrors;
        private string _ocrWarning;

        public BrainboxController(Dispatcher dispatcher, Func<ITranslationProvider> translumoProvider = null)
        {
            _dispatcher = dispatcher;
            _translumoProvider = translumoProvider;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised for short user-facing notices (tray balloon): (title, message, isError).</summary>
        public event Action<string, string, bool> Notice;

        public SerilogLog Log { get; private set; }
        public bool IsFirstRun { get; private set; }
        public SwitchableFrameSource Frames => _frames;
        public BrainboxSettings Settings => _store.Settings;
        public ScreenWatcher Watcher { get; private set; }
        public OverlayManager Overlay => _overlay;
        public VirtualDesktopKeeper Keeper => _keeper;
        public TranslationCache Cache => _cache;
        public WindowsOcrEngineAdapter Ocr => _ocr;

        public WatcherStatus Status => _status;
        public bool IsLive => _status.State is WatcherState.Live or WatcherState.Degraded;
        public bool IsPaused => Watcher?.IsPaused ?? false;

        public string StatusText => _status.State switch
        {
            WatcherState.Live => "LIVE",
            WatcherState.Degraded => "LIVE · ENGINE ISSUE",
            WatcherState.Paused => "PAUSED",
            WatcherState.Disabled => "OFF",
            WatcherState.CaptureBlocked => "WAITING FOR SCREEN",
            _ => "STARTING",
        };

        public string StatusDetail => _ocrWarning ?? (_status.State is WatcherState.Live ? "Watching every screen for foreign text" : _status.Message);

        public string LanguagePairText
        {
            get
            {
                var src = Settings.SourceLanguage != "auto"
                    ? LanguageNames.NameOf(Settings.SourceLanguage)
                    : _status.SourceLanguage != null ? LanguageNames.NameOf(_status.SourceLanguage) : "Any language";
                return $"{src} → {LanguageNames.NameOf(Settings.TargetLanguage)}";
            }
        }

        public string EngineText
        {
            get
            {
                var name = EngineFactory.DisplayName(Settings.Engine);
                var active = _status.Provider;
                return string.IsNullOrEmpty(active) || name.Contains(active, StringComparison.OrdinalIgnoreCase) ? name : $"{name} · using {active}";
            }
        }

        public string ScreenText => Settings.ScreenScope switch
        {
            ScreenScope.AllMonitors => $"All screens ({_frames?.GetMonitors().Count ?? 0})",
            ScreenScope.CurrentMonitor => "Current screen",
            _ => string.IsNullOrEmpty(Settings.SpecificMonitor) ? "Specific screen" : Settings.SpecificMonitor.Replace(@"\\.\", ""),
        };

        public string ModeText => Settings.PerformanceMode switch
        {
            PerformanceMode.BatterySaver => "Battery Saver",
            PerformanceMode.MaximumAccuracy => "Maximum Accuracy",
            _ => Settings.PerformanceMode.ToString(),
        };

        public string MetricsText => Watcher == null ? "" :
            $"OCR {Watcher.Metrics.AvgOcrMs:0} ms · translate {Watcher.Metrics.AvgTranslationMs:0} ms · " +
            $"cache {Watcher.Metrics.CacheHits}/{Watcher.Metrics.CacheHits + Watcher.Metrics.CacheMisses} · on screen {Watcher.Metrics.OverlayItems}";

        public string HotkeyErrors => _hotkeyErrors;

        // ------------------------------------------------------------------------------------

        public void Start(bool fromAutostart)
        {
            var bootLog = new SerilogLog(BrainboxPaths.Logs);
            Log = bootLog;
            Log.Info($"Brainbox Live Translator starting (autostart={fromAutostart}, OS {Environment.OSVersion.Version}).");

            _store = new SettingsStore(BrainboxPaths.Settings, Log);
            Log.Enabled = Settings.LoggingEnabled;
            Log.SetDebug(Settings.DebugMode);

            IsFirstRun = !Settings.FirstRunCompleted;
            if (!Settings.FirstRunCompleted)
            {
                Settings.FirstRunCompleted = true;
                _store.SaveNow();
            }

            // Spec: translation switches itself on at launch unless the user opted out.
            Settings.LiveTranslationEnabled = Settings.AutoEnableOnStart || (!fromAutostart && Settings.LiveTranslationEnabled);

            try
            {
                _cache = new TranslationCache(new SqliteTranslationStore(BrainboxPaths.Cache), log: Log);
            }
            catch (Exception ex)
            {
                Log.Warn("Persistent cache unavailable; using memory cache only.", ex);
                _cache = new TranslationCache(log: Log);
            }

            _frames = new SwitchableFrameSource(Settings.CaptureBackend, Log);
            Log.Info("Capture backend: " + _frames.BackendName);
            _ocr = new WindowsOcrEngineAdapter();
            if (!_ocr.IsAvailable)
            {
                _ocrWarning = "No Windows OCR language is installed. Add a language (e.g. Japanese) in Windows Settings → Time & language.";
                Log.Error(_ocrWarning);
            }
            else
            {
                Log.Info("Windows OCR recognizers: " + string.Join(", ", _ocr.InstalledLanguageTags));
            }

            var (chain, vision, owned) = EngineFactory.Build(Settings, () => Environment.TickCount64, Log, _translumoProvider);
            _engineOwned.AddRange(owned);
            Watcher = new ScreenWatcher(_frames, _ocr, chain, _cache, Settings, Log);
            Watcher.SetVisionTranslator(vision);
            Watcher.ForegroundWindowBounds = ForegroundBounds;

            _overlay = new OverlayManager(Settings, _dispatcher, Log);
            _overlay.SyncMonitors(_frames.GetMonitors());

            Watcher.OverlayUpdated += _overlay.Show;
            Watcher.ActivityChanged += _overlay.SetActivity;
            Watcher.MonitorsChanged += monitors => _dispatcher.BeginInvoke(new Action(() =>
            {
                _overlay.SyncMonitors(monitors);
                Raise(nameof(ScreenText));
            }));
            Watcher.StatusChanged += s => _dispatcher.BeginInvoke(new Action(() => OnStatus(s)));

            _keeper = new VirtualDesktopKeeper(() => _overlay.WindowHandles, () => Watcher.RequestFullRescan(), Log)
            {
                Enabled = Settings.FollowVirtualDesktops,
            };

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _hotkeys = new GlobalHotkeys();
            ApplyHotkeys();
            ApplyStartup();
            Settings.PropertyChanged += OnSettingsChanged;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (_, _) =>
            {
                Raise(nameof(MetricsText));
                _overlay.BringToTop();
            };
            _uiTimer.Start();

            Watcher.Start();
            UpdateGlow();
            RaiseAll();
        }

        public void ToggleLive() => Settings.LiveTranslationEnabled = !Settings.LiveTranslationEnabled;

        public void TogglePause()
        {
            if (Watcher.IsPaused) Watcher.Resume();
            else Watcher.Pause();
            UpdateGlow();
            Raise(nameof(IsPaused));
        }

        public void ToggleGlow() => Settings.GlowEnabled = !Settings.GlowEnabled;

        /// <summary>"Restart Translator": rebuilds engine + capture state without restarting the process.</summary>
        public async Task RestartAsync()
        {
            Log.Info("Restarting translator.");
            await Watcher.StopAsync();
            RebuildEngine();
            _overlay.SyncMonitors(_frames.GetMonitors());
            Watcher.RequestFullRescan();
            Watcher.Start();
            UpdateGlow();
            RaiseAll();
        }

        private void RebuildEngine()
        {
            foreach (var d in _engineOwned) d.Dispose();
            _engineOwned.Clear();
            var (chain, vision, owned) = EngineFactory.Build(Settings, () => Environment.TickCount64, Log, _translumoProvider);
            _engineOwned.AddRange(owned);
            Watcher.SetTranslator(chain, vision);
            Raise(nameof(EngineText));
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(BrainboxSettings.Engine):
                case nameof(BrainboxSettings.LmStudioEndpoint):
                case nameof(BrainboxSettings.OllamaEndpoint):
                case nameof(BrainboxSettings.CustomEndpoint):
                case nameof(BrainboxSettings.CustomApiKey):
                case nameof(BrainboxSettings.Model):
                case nameof(BrainboxSettings.CloudFallback):
                case nameof(BrainboxSettings.RequestTimeoutSeconds):
                    _dispatcher.BeginInvoke(new Action(RebuildEngine), DispatcherPriority.Background);
                    break;
                case nameof(BrainboxSettings.GlowEnabled):
                case nameof(BrainboxSettings.LiveTranslationEnabled):
                    UpdateGlow();
                    break;
                case nameof(BrainboxSettings.GlowIntensity):
                    _overlay.ApplyGlowIntensity();
                    break;
                case nameof(BrainboxSettings.StartWithWindows):
                    ApplyStartup();
                    break;
                case nameof(BrainboxSettings.HotkeysEnabled):
                case nameof(BrainboxSettings.HotkeyToggle):
                case nameof(BrainboxSettings.HotkeyPause):
                case nameof(BrainboxSettings.HotkeyGlow):
                    ApplyHotkeys();
                    break;
                case nameof(BrainboxSettings.CaptureBackend):
                    _frames.Switch(Settings.CaptureBackend);
                    Log.Info("Capture backend: " + _frames.BackendName);
                    Watcher.RequestFullRescan();
                    break;
                case nameof(BrainboxSettings.FollowVirtualDesktops):
                    _keeper.Enabled = Settings.FollowVirtualDesktops;
                    break;
                case nameof(BrainboxSettings.LoggingEnabled):
                    Log.Enabled = Settings.LoggingEnabled;
                    break;
                case nameof(BrainboxSettings.DebugMode):
                    Log.SetDebug(Settings.DebugMode);
                    break;
            }

            RaiseAll();
        }

        private void ApplyHotkeys()
        {
            _hotkeys.UnregisterAll();
            if (!Settings.HotkeysEnabled)
            {
                _hotkeyErrors = null;
                Raise(nameof(HotkeyErrors));
                return;
            }

            var errors = new[]
            {
                _hotkeys.Register(Settings.HotkeyToggle, () => _dispatcher.BeginInvoke(new Action(ToggleLive))),
                _hotkeys.Register(Settings.HotkeyPause, () => _dispatcher.BeginInvoke(new Action(TogglePause))),
                _hotkeys.Register(Settings.HotkeyGlow, () => _dispatcher.BeginInvoke(new Action(ToggleGlow))),
            }.Where(x => x != null).ToList();
            _hotkeyErrors = errors.Count == 0 ? null : string.Join(" ", errors);
            if (_hotkeyErrors != null) Log.Warn("Hotkeys: " + _hotkeyErrors);
            Raise(nameof(HotkeyErrors));
        }

        private void ApplyStartup()
        {
            if (!StartupManager.Apply(Settings.StartWithWindows)) Log.Warn("Could not update the Windows startup entry.");
        }

        private void UpdateGlow()
        {
            if (_overlay == null || Watcher == null) return;
            var visible = Settings.GlowEnabled && Settings.LiveTranslationEnabled && !Watcher.IsPaused;
            _overlay.SetGlowVisible(visible);
        }

        private WatcherState _lastNotified = WatcherState.Live;
        private DateTime _lastNoticeAt = DateTime.MinValue;

        private void OnStatus(WatcherStatus s)
        {
            var previous = _status;
            _status = s;
            UpdateGlow();
            RaiseAll();

            // Small, rate-limited status notifications (§20).
            if (s.State == _lastNotified) return;
            var now = DateTime.UtcNow;
            if (s.State == WatcherState.Degraded && (now - _lastNoticeAt).TotalSeconds > 45)
            {
                Notice?.Invoke("Translation engine issue", s.Message, true);
                _lastNoticeAt = now;
                _lastNotified = s.State;
            }
            else if (s.State == WatcherState.Live && previous.State == WatcherState.Degraded)
            {
                Notice?.Invoke("Brainbox is back", $"{s.Provider ?? "Translation engine"} is responding again.", false);
                _lastNotified = s.State;
            }
            else if (s.State is WatcherState.Live or WatcherState.Paused or WatcherState.Disabled)
            {
                _lastNotified = s.State;
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Log.Info("Display settings changed (resolution/DPI/monitors).");
                _overlay.SyncMonitors(_frames.GetMonitors());
                Watcher.RequestFullRescan();
                Raise(nameof(ScreenText));
            }));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            {
                Watcher.RequestFullRescan();
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Watcher.RequestFullRescan();
        }

        private static PixelRect? ForegroundBounds()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || !Native.GetWindowRect(fg, out var r)) return null;
            return PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        private void RaiseAll()
        {
            foreach (var p in new[] { nameof(Status), nameof(IsLive), nameof(IsPaused), nameof(StatusText), nameof(StatusDetail), nameof(LanguagePairText), nameof(EngineText), nameof(ScreenText), nameof(ModeText), nameof(MetricsText) })
                Raise(p);
        }

        private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _uiTimer?.Stop();
            try
            {
                Watcher?.StopAsync().Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            Watcher?.Dispose();
            _keeper?.Dispose();
            _hotkeys?.Dispose();
            _overlay?.Dispose();
            foreach (var d in _engineOwned) d.Dispose();
            _frames?.Dispose();
            _cache?.Dispose();
            _store?.Dispose();
            Log?.Info("Brainbox stopped.");
            Log?.Dispose();
        }
    }
}
