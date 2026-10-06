using System;
using System.Threading;
using System.Windows;
using Brainbox.Core.Translation;
using Brainbox.Desktop.UI;
using Translumo;

namespace Brainbox.Desktop.Shell
{
    /// <summary>Signals an already-running instance to show its window.</summary>
    public static class SingleInstance
    {
        public const string ShowEventName = @"Local\BrainboxLiveTranslator.Show";

        public static void SignalShow()
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
        }
    }

    /// <summary>Application shell: controller + tray + windows + single-instance "show" listener.</summary>
    public sealed class BrainboxShell : IDisposable
    {
        private readonly App _app;
        private readonly Action _showClassic;
        private readonly BrainboxController _controller;
        private readonly EventWaitHandle _showEvent;
        private readonly RegisteredWaitHandle _showWait;
        private TrayIcon _tray;
        private MainWindow _main;
        private SettingsWindow _settings;

        public BrainboxShell(App app, Func<ITranslationProvider> translumoProvider, Action showClassic)
        {
            _app = app;
            _showClassic = showClassic;
            _controller = new BrainboxController(app.Dispatcher, translumoProvider);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstance.ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => app.Dispatcher.BeginInvoke(new Action(ShowMain)), null, Timeout.Infinite, false);
        }

        public BrainboxController Controller => _controller;

        public void Start(bool fromAutostart)
        {
            _controller.Start(fromAutostart);
            _tray = new TrayIcon(_controller, ShowMain, ShowSettings, _showClassic, () => _app.ExitApplication());

            var minimized = fromAutostart || _controller.Settings.StartMinimized;
            if (!minimized || _controller.IsFirstRun) ShowMain();
        }

        public void ShowMain()
        {
            if (_main == null)
            {
                _main = new MainWindow(_controller, ShowSettings);
                _main.HiddenToTray += () => _controller.Log.Info("Main window hidden to tray.");
            }

            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        public void ShowSettings()
        {
            if (_settings == null || !_settings.IsLoaded)
            {
                _settings = new SettingsWindow(_controller, _showClassic);
                _settings.Closed += (_, _) => _settings = null;
            }

            _settings.Show();
            _settings.Activate();
        }

        public void Dispose()
        {
            _showWait.Unregister(null);
            _showEvent.Dispose();
            _tray?.Dispose();
            _controller.Dispose();
        }
    }
}
