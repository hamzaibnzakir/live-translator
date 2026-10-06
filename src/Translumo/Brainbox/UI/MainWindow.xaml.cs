using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brainbox.Core.Pipeline;
using Brainbox.Desktop.Shell;

namespace Brainbox.Desktop.UI
{
    /// <summary>Main Brainbox window (§18). Closing it hides to the tray; translation keeps running.</summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly Action _openSettings;
        private bool _hintShown;

        public MainWindow(BrainboxController controller, Action openSettings)
        {
            Controller = controller;
            _openSettings = openSettings;
            InitializeComponent();
            DataContext = this;
            controller.PropertyChanged += (_, e) =>
            {
                Raise(nameof(StatusBrush));
                Raise(nameof(PauseText));
                Raise(nameof(HasHotkeyErrors));
                Raise(nameof(HotkeyHint));
            };
            Loaded += (_, _) => StartPulse();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised the first time the window is hidden to the tray.</summary>
        public event Action HiddenToTray;

        public BrainboxController Controller { get; }

        public Brush StatusBrush => new SolidColorBrush(Controller.Status.State switch
        {
            WatcherState.Live => Color.FromRgb(0x34, 0xD3, 0x99),
            WatcherState.Degraded => Color.FromRgb(0xFB, 0xBF, 0x24),
            WatcherState.CaptureBlocked => Color.FromRgb(0xFB, 0xBF, 0x24),
            _ => Color.FromRgb(0x8A, 0x90, 0xA2),
        });

        public string PauseText => Controller.IsPaused ? "Resume" : "Pause";

        public bool HasHotkeyErrors => !string.IsNullOrEmpty(Controller.HotkeyErrors);

        public string HotkeyHint => Controller.Settings.HotkeysEnabled
            ? $"{Controller.Settings.HotkeyToggle} on/off · {Controller.Settings.HotkeyPause} pause · {Controller.Settings.HotkeyGlow} glow"
            : "Hotkeys are off";

        private void StartPulse()
        {
            var pulse = new DoubleAnimation(0.15, 0.55, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, 20);
            StatusHalo.BeginAnimation(OpacityProperty, pulse);
        }

        private void OnPause(object sender, RoutedEventArgs e) => Controller.TogglePause();

        private void OnSettings(object sender, RoutedEventArgs e) => _openSettings();

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                HiddenToTray?.Invoke();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Alt+F4 also only hides; Exit lives in the tray menu.
            if (!((App)Application.Current).IsShuttingDown)
            {
                e.Cancel = true;
                OnClose(this, null);
            }

            base.OnClosing(e);
        }

        private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
