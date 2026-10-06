using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpDX.XInput;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Translumo.Configuration;
using Translumo.Dialog;
using Translumo.HotKeys;
using Translumo.Infrastructure.Constants;
using Translumo.Infrastructure.Dispatching;
using Translumo.Infrastructure.Encryption;
using Translumo.Infrastructure.Language;
using Translumo.Infrastructure.MachineLearning;
using Translumo.Infrastructure.Python;
using Translumo.MVVM.Models;
using Translumo.MVVM.ViewModels;
using Translumo.OCR;
using Translumo.OCR.Configuration;
using Translumo.Processing;
using Translumo.Processing.Configuration;
using Translumo.Processing.Interfaces;
using Translumo.Processing.TextProcessing;
using Translumo.Services;
using Translumo.Translation;
using Translumo.Translation.Llm;
using Translumo.Translation.Configuration;
using Translumo.TTS;
using Translumo.Update;
using Translumo.Utils;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Translumo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public App()
        {
            Log.Logger = CreateLogger();

            ServiceCollection services = new ServiceCollection();
            ConfigureServices(services);
            this._serviceProvider = services.BuildServiceProvider();
            this._logger = _serviceProvider.GetService<ILogger<App>>();

            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        }

        private void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            _logger.LogCritical(e.ExceptionObject as Exception, "Unhandled app exception");
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogCritical(e.Exception, "Unhandled app exception");
        }

        /// <summary>True once Exit was chosen (windows stop hiding themselves to the tray).</summary>
        public bool IsShuttingDown { get; private set; }

        private Brainbox.Desktop.Shell.BrainboxShell _shell;
        private System.Threading.Mutex _singleInstance;
        private bool _classicStarted;

        protected override void OnExit(ExitEventArgs e)
        {
            IsShuttingDown = true;
            try
            {
                _shell?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Brainbox shutdown failed");
            }

            base.OnExit(e);

            var configurationStorage = _serviceProvider.GetService<ConfigurationStorage>();
            configurationStorage.SaveConfiguration();

            var llmProfiles = _serviceProvider.GetService<LlmProfiles>();
            llmProfiles?.Save();
        }

        /// <summary>Exit from the tray menu.</summary>
        public void ExitApplication()
        {
            IsShuttingDown = true;
            Shutdown();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Keep the .NET single-file bundle extraction temp from accumulating on the
            // system (C:) drive. Redirects future extractions next to the executable and
            // reaps stale orphaned directories from prior runs/crashes. Best-effort only.
            try
            {
                TempBundleCleaner.ConfigureLocalExtractionBase();
                TempBundleCleaner.CleanStaleExtractions(TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Single-file extraction temp cleanup failed");
            }

            var args = e.Args ?? Array.Empty<string>();
            var selfTest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

            if (!selfTest)
            {
                _singleInstance = new System.Threading.Mutex(true, @"Local\BrainboxLiveTranslator", out var isFirst);
                if (!isFirst)
                {
                    // Already running: ask the running instance to show its window, then quit.
                    Brainbox.Desktop.Shell.SingleInstance.SignalShow();
                    Shutdown();
                    return;
                }
            }

            var configurationStorage = _serviceProvider.GetService<ConfigurationStorage>();
            configurationStorage.LoadConfiguration();
            ConfigurationStorage.EnsureEncryptionKey();

            // Apply the persisted UI theme before any window is shown so there is no light flash.
            ThemeService.Load();
            ThemeService.Apply(ThemeService.Current);

            if (selfTest)
            {
                _ = Brainbox.Desktop.SelfTest.SelfTestRunner.RunAsync(this, args, CreateTranslumoProvider);
                return;
            }

            var autostart = args.Any(a => a.Equals(Brainbox.Desktop.Shell.StartupManager.AutostartArg, StringComparison.OrdinalIgnoreCase));
            _shell = new Brainbox.Desktop.Shell.BrainboxShell(this, CreateTranslumoProvider, ShowClassicTranslator);
            _shell.Start(autostart);
        }

        /// <summary>The original Translumo region translator, opened on demand from the tray.</summary>
        public void ShowClassicTranslator()
        {
            var chatViewModel = _serviceProvider.GetService<ChatWindowViewModel>();
            var dialogService = _serviceProvider.GetService<DialogService>();
            _ = dialogService.ShowWindowAsync(chatViewModel);
            if (!_classicStarted)
            {
                _classicStarted = true;
                _serviceProvider.RegisterUIInputController();
            }
        }

        /// <summary>
        /// Brainbox engine "Translumo cloud AI profile": the active Translumo LLM profile with a
        /// source-auto-detect prompt (or Google auto-detect when the classic translator is not LLM).
        /// </summary>
        private Brainbox.Core.Translation.ITranslationProvider CreateTranslumoProvider()
        {
            var translationConfiguration = _serviceProvider.GetService<TranslationConfiguration>();
            var llmProfiles = _serviceProvider.GetService<LlmProfiles>();
            var languageService = _serviceProvider.GetService<LanguageService>();
            var active = translationConfiguration.Translator == Translators.Llm ? llmProfiles.Active : null;
            if (active == null || !active.Enabled)
            {
                return Brainbox.Desktop.Shell.EngineFactory.Google();
            }

            var llm = new LlmTranslator(translationConfiguration, active, languageService, _logger);
            return new Brainbox.Core.Translation.DelegateTranslationProvider("Translumo " + active.Name, isLocal: active.Provider == LlmProvider.Ollama,
                (text, target, source, ct) => llm.TranslateAutoDetectAsync(text, Brainbox.Core.Translation.LanguageNames.NameOf(target)), maxParallel: 3);
        }

        private void ConfigureServices(ServiceCollection services)
        {
            services.AddLogging(builder => builder.AddSerilog(/*Log.Logger,*/ dispose: true));

            services.AddScoped<SettingsViewModel>();
            services.AddScoped<AppearanceSettingsViewModel>();
            services.AddScoped<HotkeysSettingsViewModel>();
            services.AddScoped<LanguagesSettingsViewModel>();
            services.AddScoped<OcrSettingsViewModel>();

            var chatWindowConfiguration = ChatWindowConfiguration.Default;
            services.AddSingleton<OcrGeneralConfiguration>(OcrGeneralConfiguration.Default);
            services.AddSingleton<TranslationConfiguration>(TranslationConfiguration.Default);
            services.AddSingleton<TtsConfiguration>(TtsConfiguration.Default);
            services.AddSingleton<ChatWindowConfiguration>(chatWindowConfiguration);
            services.AddSingleton<HotKeysConfiguration>(HotKeysConfiguration.Default);
            services.AddSingleton<SystemConfiguration>(SystemConfiguration.Default);
            services.AddSingleton<LlmProfiles>(new LlmProfiles());
            services.AddSingleton<TextProcessingConfiguration>(chatWindowConfiguration.TextProcessing);

            var chatMediatorInstance = new ChatUITextMediator();
            services.AddSingleton<IChatTextMediator, ChatUITextMediator>(provider => chatMediatorInstance);
            services.AddSingleton<ChatUITextMediator>(chatMediatorInstance);
            services.AddSingleton<ILocalizationProvider, LocalizationProvider>();
            services.AddSingleton<ChatWindowViewModel>();
            services.AddSingleton<ChatWindowModel>();
            services.AddSingleton<HotKeysServiceManager>();
            services.AddSingleton<ScreenCaptureConfiguration>();
            services.AddSingleton<DialogService>();
            services.AddSingleton<LanguageService>();
            services.AddSingleton<TextDetectionProvider>();
            services.AddSingleton<IActionDispatcher, InteractionActionDispatcher>();
            services.AddSingleton<TextValidityPredictor>();
            services.AddSingleton<IControllerService, GamepadService>();
            services.AddSingleton<IControllerInputProvider, ControllerInputProvider>();
            services.AddSingleton<ObservablePipe<Keystroke>>(new ObservablePipe<Keystroke>(Application.Current.Dispatcher));
            services.AddSingleton<UpdateManager>();
            // In-app update checks run against THIS fork's GitHub releases (tag vX.Y.Z) so the
            // "new version available" notification reflects this repo, not the original upstream.
            services.AddSingleton<IReleasesClient, GithubApiClient>(provider => new GithubApiClient("HenryNebula", "Translumo-AI"));
            services.AddSingleton<ICapturerFactory, ScreenCapturerFactory>();
            services.AddSingleton<PythonEngineWrapper>();

            services.AddSingleton<Processing.ImageTranslation.ImageTranslationService>();

            services.AddTransient<IProcessingService, TranslationProcessingService>();
            services.AddTransient<OcrEnginesFactory>();
            services.AddTransient<TranslatorFactory>();
            services.AddTransient<TextResultCacheService>();
            services.AddTransient<IPredictor<InputTextPrediction, OutputTextPrediction>, MlPredictor<InputTextPrediction, OutputTextPrediction>>();
            services.AddTransient<IEncryptionService, AesEncryptionService>();
            services.AddTransient<LanguageDescriptorFactory>();
            services.AddTransient<TtsFactory>();


            services.AddConfigurationStorage();
        }

        private Logger CreateLogger()
        {
            var configuration = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Verbose()
                .WriteTo.File("Logs/log.txt", LogEventLevel.Warning, rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}", retainedFileCountLimit: 10);

#if DEBUG
            configuration = configuration.WriteTo.File("Logs/trace.txt", LogEventLevel.Verbose, rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}");
#endif

            return configuration.CreateLogger();
        }

    }

    public static class NativeDialog
    {
        private const int MB_OK = 0x0;
        private const int MB_ICONERROR = 0x10;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        public static void ShowError(string message, string title = "Error")
        {
            // hWnd = IntPtr.Zero means no owner window
            MessageBoxW(IntPtr.Zero, message, title, MB_OK | MB_ICONERROR);
        }
    }
}
