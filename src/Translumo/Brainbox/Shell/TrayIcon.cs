using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brainbox.Core.Pipeline;
using Brainbox.Core.Settings;
using Brainbox.Core.Translation;
using Hardcodet.Wpf.TaskbarNotification;

namespace Brainbox.Desktop.Shell
{
    /// <summary>System-tray presence (§12) — the app lives here; the main window is optional.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly BrainboxController _c;
        private readonly Action _showMain;
        private readonly Action _showSettings;
        private readonly Action _showClassic;
        private readonly Action _exit;
        private readonly TaskbarIcon _icon;
        private readonly System.Drawing.Icon _liveIcon;
        private readonly System.Drawing.Icon _pausedIcon;

        private MenuItem _status;
        private MenuItem _target;
        private MenuItem _engine;
        private MenuItem _mode;
        private MenuItem _glow;
        private MenuItem _pause;
        private MenuItem _live;

        public TrayIcon(BrainboxController controller, Action showMain, Action showSettings, Action showClassic, Action exit)
        {
            _c = controller;
            _showMain = showMain;
            _showSettings = showSettings;
            _showClassic = showClassic;
            _exit = exit;
            _liveIcon = LoadIcon("brainbox.ico");
            _pausedIcon = LoadIcon("brainbox_paused.ico");

            _icon = new TaskbarIcon
            {
                Icon = _liveIcon,
                ToolTipText = "Brainbox Live Translator",
                ContextMenu = BuildMenu(),
                MenuActivation = PopupActivationMode.RightClick,
            };
            _icon.TrayLeftMouseUp += (_, _) => _showMain();
            _c.PropertyChanged += OnControllerChanged;
            _c.Settings.PropertyChanged += OnControllerChanged;
            _c.Notice += (title, msg, error) => _icon.ShowBalloonTip(title, msg, error ? BalloonIcon.Warning : BalloonIcon.Info);
            Refresh();
        }

        private static System.Drawing.Icon LoadIcon(string name)
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Resources/Brainbox/{name}"));
            return info == null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(info.Stream);
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var header = new MenuItem { Header = "Brainbox Translator", FontWeight = FontWeights.SemiBold, IsEnabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new Separator());

            _status = new MenuItem { IsEnabled = false };
            menu.Items.Add(_status);

            _live = new MenuItem { Header = "Live Translation", IsCheckable = true };
            _live.Click += (_, _) => _c.ToggleLive();
            menu.Items.Add(_live);
            menu.Items.Add(new Separator());

            _target = new MenuItem();
            foreach (var (code, name) in LanguageNames.All.Take(16))
            {
                var item = new MenuItem { Header = name, Tag = code, IsCheckable = true };
                item.Click += (_, _) => _c.Settings.TargetLanguage = code;
                _target.Items.Add(item);
            }

            menu.Items.Add(_target);

            _engine = new MenuItem();
            foreach (TranslationEngineKind kind in Enum.GetValues(typeof(TranslationEngineKind)))
            {
                var item = new MenuItem { Header = EngineFactory.DisplayName(kind), Tag = kind, IsCheckable = true };
                item.Click += (_, _) => _c.Settings.Engine = kind;
                _engine.Items.Add(item);
            }

            menu.Items.Add(_engine);

            _mode = new MenuItem();
            foreach (PerformanceMode m in Enum.GetValues(typeof(PerformanceMode)))
            {
                var item = new MenuItem { Header = ModeName(m), Tag = m, IsCheckable = true };
                item.Click += (_, _) => _c.Settings.PerformanceMode = m;
                _mode.Items.Add(item);
            }

            menu.Items.Add(_mode);

            _glow = new MenuItem();
            var on = new MenuItem { Header = "ON", Tag = true, IsCheckable = true };
            on.Click += (_, _) => _c.Settings.GlowEnabled = true;
            var off = new MenuItem { Header = "OFF", Tag = false, IsCheckable = true };
            off.Click += (_, _) => _c.Settings.GlowEnabled = false;
            _glow.Items.Add(on);
            _glow.Items.Add(off);
            menu.Items.Add(_glow);
            menu.Items.Add(new Separator());

            _pause = new MenuItem();
            _pause.Click += (_, _) => _c.TogglePause();
            menu.Items.Add(_pause);

            var open = new MenuItem { Header = "Open Brainbox" };
            open.Click += (_, _) => _showMain();
            menu.Items.Add(open);

            var settings = new MenuItem { Header = "Settings" };
            settings.Click += (_, _) => _showSettings();
            menu.Items.Add(settings);

            var classic = new MenuItem { Header = "Classic region translator (Translumo)" };
            classic.Click += (_, _) => _showClassic();
            menu.Items.Add(classic);

            var restart = new MenuItem { Header = "Restart Translator" };
            restart.Click += async (_, _) => await _c.RestartAsync();
            menu.Items.Add(restart);
            menu.Items.Add(new Separator());

            var exit = new MenuItem { Header = "Exit" };
            exit.Click += (_, _) => _exit();
            menu.Items.Add(exit);
            return menu;
        }

        private static string ModeName(PerformanceMode m) => m switch
        {
            PerformanceMode.BatterySaver => "Battery Saver",
            PerformanceMode.MaximumAccuracy => "Maximum Accuracy",
            _ => m.ToString(),
        };

        private void OnControllerChanged(object sender, PropertyChangedEventArgs e) =>
            _icon.Dispatcher.BeginInvoke(new Action(Refresh));

        private void Refresh()
        {
            var s = _c.Settings;
            var state = _c.Status.State;
            var live = s.LiveTranslationEnabled && !_c.IsPaused;
            _status.Header = state switch
            {
                WatcherState.Live => "● Live Translation ON",
                WatcherState.Degraded => "● Live — engine issue (retrying)",
                WatcherState.Paused => "❚❚ Paused",
                WatcherState.Disabled => "○ Live Translation OFF",
                WatcherState.CaptureBlocked => "◌ Waiting for screen access",
                _ => "○ Starting…",
            };
            _status.Foreground = new SolidColorBrush(state switch
            {
                WatcherState.Live => Color.FromRgb(0x16, 0xA3, 0x4A),
                WatcherState.Degraded => Color.FromRgb(0xD9, 0x77, 0x06),
                _ => Color.FromRgb(0x6B, 0x72, 0x80),
            });
            _live.IsChecked = s.LiveTranslationEnabled;
            _target.Header = "Target Language → " + LanguageNames.NameOf(s.TargetLanguage);
            foreach (MenuItem i in _target.Items) i.IsChecked = (string)i.Tag == s.TargetLanguage;
            _engine.Header = "Translation Engine → " + EngineFactory.DisplayName(s.Engine);
            foreach (MenuItem i in _engine.Items) i.IsChecked = (TranslationEngineKind)i.Tag == s.Engine;
            _mode.Header = "Mode → " + ModeName(s.PerformanceMode);
            foreach (MenuItem i in _mode.Items) i.IsChecked = (PerformanceMode)i.Tag == s.PerformanceMode;
            _glow.Header = "Glow → " + (s.GlowEnabled ? "ON" : "OFF");
            foreach (MenuItem i in _glow.Items) i.IsChecked = (bool)i.Tag == s.GlowEnabled;
            _pause.Header = _c.IsPaused ? "Resume Translation" : "Pause Translation";
            _icon.Icon = live ? _liveIcon : _pausedIcon;
            _icon.ToolTipText = $"Brainbox Live Translator — {_c.StatusText}\n{_c.LanguagePairText}";
        }

        public void Dispose()
        {
            _c.PropertyChanged -= OnControllerChanged;
            _c.Settings.PropertyChanged -= OnControllerChanged;
            _icon.Dispose();
        }
    }
}
