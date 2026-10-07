using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brainbox.Core.Settings;

public enum TranslationEngineKind
{
    /// <summary>LM Studio's OpenAI-compatible server (default http://localhost:1234/v1).</summary>
    LmStudio,
    /// <summary>Ollama's OpenAI-compatible endpoint (default http://localhost:11434/v1).</summary>
    Ollama,
    /// <summary>Any other OpenAI-compatible endpoint (llama.cpp, vLLM, LocalAI, OpenRouter...).</summary>
    CustomOpenAi,
    /// <summary>Google Translate (online, no key).</summary>
    Google,
    /// <summary>The translator configured in the classic Translumo settings (DeepL / Yandex / Papago / LLM profile).</summary>
    Translumo,
}

public enum PerformanceMode { Balanced, Performance, BatterySaver, MaximumAccuracy }

public enum ScreenScope { AllMonitors, CurrentMonitor, SpecificMonitor }

public enum OverlayStyle { Replace, Subtitle, Bubble }

public enum OverlayBackground { Auto, Dark, Light, Blur }

public enum CaptureBackend { Auto, Gdi, DesktopDuplication }

/// <summary>Observable base for settings bound to WPF.</summary>
public abstract class ObservableSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>All user settings (§19). Persisted as JSON in %LOCALAPPDATA%\BrainboxLiveTranslator.</summary>
public sealed class BrainboxSettings : ObservableSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // --- General / state -------------------------------------------------------------
    private bool _liveTranslationEnabled = true;
    public bool LiveTranslationEnabled { get => _liveTranslationEnabled; set => Set(ref _liveTranslationEnabled, value); }

    // --- Translation -------------------------------------------------------------------
    private string _sourceLanguage = "auto";
    public string SourceLanguage { get => _sourceLanguage; set => Set(ref _sourceLanguage, string.IsNullOrWhiteSpace(value) ? "auto" : value); }

    private string _targetLanguage = "en";
    public string TargetLanguage { get => _targetLanguage; set => Set(ref _targetLanguage, string.IsNullOrWhiteSpace(value) ? "en" : value); }

    private TranslationEngineKind _engine = TranslationEngineKind.LmStudio;
    public TranslationEngineKind Engine { get => _engine; set => Set(ref _engine, value); }

    private string _lmStudioEndpoint = "http://localhost:1234/v1";
    public string LmStudioEndpoint { get => _lmStudioEndpoint; set => Set(ref _lmStudioEndpoint, value); }

    private string _ollamaEndpoint = "http://localhost:11434/v1";
    public string OllamaEndpoint { get => _ollamaEndpoint; set => Set(ref _ollamaEndpoint, value); }

    private string _customEndpoint = "";
    public string CustomEndpoint { get => _customEndpoint; set => Set(ref _customEndpoint, value); }

    /// <summary>API key for the custom endpoint. Encrypted at rest by the Windows layer (DPAPI).</summary>
    private string _customApiKey = "";
    public string CustomApiKey { get => _customApiKey; set => Set(ref _customApiKey, value); }

    /// <summary>Model id; empty = use whatever model the local server has loaded.</summary>
    private string _model = "";
    public string Model { get => _model; set => Set(ref _model, value ?? ""); }

    private int _contextSize = 6;
    public int ContextSize { get => _contextSize; set => Set(ref _contextSize, Math.Clamp(value, 0, 30)); }

    /// <summary>Fall back to Google Translate when the local engine is unavailable (sends text online).</summary>
    private bool _cloudFallback;
    public bool CloudFallback { get => _cloudFallback; set => Set(ref _cloudFallback, value); }

    private int _requestTimeoutSeconds = 30;
    public int RequestTimeoutSeconds { get => _requestTimeoutSeconds; set => Set(ref _requestTimeoutSeconds, Math.Clamp(value, 3, 300)); }

    // --- Screen ------------------------------------------------------------------------
    private ScreenScope _screenScope = ScreenScope.AllMonitors;
    public ScreenScope ScreenScope { get => _screenScope; set => Set(ref _screenScope, value); }

    private string _specificMonitor = "";
    public string SpecificMonitor { get => _specificMonitor; set => Set(ref _specificMonitor, value ?? ""); }

    /// <summary>Keep the overlay/glow on whichever Windows virtual desktop is active.</summary>
    private bool _followVirtualDesktops = true;
    public bool FollowVirtualDesktops { get => _followVirtualDesktops; set => Set(ref _followVirtualDesktops, value); }

    // --- Performance -------------------------------------------------------------------
    private PerformanceMode _performanceMode = PerformanceMode.Balanced;
    public PerformanceMode PerformanceMode { get => _performanceMode; set => Set(ref _performanceMode, value); }

    /// <summary>Capture interval override in ms (0 = use the performance mode's value).</summary>
    private int _detectionIntervalMs;
    public int DetectionIntervalMs { get => _detectionIntervalMs; set => Set(ref _detectionIntervalMs, value <= 0 ? 0 : Math.Clamp(value, 100, 10000)); }

    /// <summary>Change-detection sensitivity 1 (only big changes) … 10 (every pixel).</summary>
    private int _sensitivity = 6;
    public int Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, Math.Clamp(value, 1, 10)); }

    private string _ocrEngine = "Windows OCR";
    public string OcrEngine { get => _ocrEngine; set => Set(ref _ocrEngine, value); }

    private CaptureBackend _captureBackend = CaptureBackend.Auto;
    public CaptureBackend CaptureBackend { get => _captureBackend; set => Set(ref _captureBackend, value); }

    /// <summary>Ask a vision model when OCR returns garbage (Maximum Accuracy mode only).</summary>
    private bool _visionFallback;
    public bool VisionFallback { get => _visionFallback; set => Set(ref _visionFallback, value); }

    // --- Overlay -----------------------------------------------------------------------
    private double _overlayOpacity = 1.0;
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, Math.Clamp(value, 0.3, 1.0)); }

    private string _fontFamily = "Segoe UI";
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value); }

    /// <summary>Font size in DIPs; 0 = match the size of the original text.</summary>
    private double _fontSize;
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value <= 0 ? 0 : Math.Clamp(value, 8, 48)); }

    private OverlayBackground _overlayBackground = OverlayBackground.Auto;
    public OverlayBackground OverlayBackground { get => _overlayBackground; set => Set(ref _overlayBackground, value); }

    private OverlayStyle _overlayStyle = OverlayStyle.Replace;
    public OverlayStyle OverlayStyle { get => _overlayStyle; set => Set(ref _overlayStyle, value); }

    private bool _glowEnabled = true;
    public bool GlowEnabled { get => _glowEnabled; set => Set(ref _glowEnabled, value); }

    private double _glowIntensity = 0.5;
    public double GlowIntensity { get => _glowIntensity; set => Set(ref _glowIntensity, Math.Clamp(value, 0.05, 1.0)); }

    // --- Startup -----------------------------------------------------------------------
    private bool _startWithWindows = true;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    private bool _autoEnableOnStart = true;
    public bool AutoEnableOnStart { get => _autoEnableOnStart; set => Set(ref _autoEnableOnStart, value); }

    private bool _startMinimized = true;
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    // --- Hotkeys -----------------------------------------------------------------------
    private bool _hotkeysEnabled = true;
    public bool HotkeysEnabled { get => _hotkeysEnabled; set => Set(ref _hotkeysEnabled, value); }

    private string _hotkeyToggle = "Ctrl+Alt+B";
    public string HotkeyToggle { get => _hotkeyToggle; set => Set(ref _hotkeyToggle, value ?? ""); }

    private string _hotkeyPause = "Ctrl+Alt+P";
    public string HotkeyPause { get => _hotkeyPause; set => Set(ref _hotkeyPause, value ?? ""); }

    private string _hotkeyGlow = "Ctrl+Alt+G";
    public string HotkeyGlow { get => _hotkeyGlow; set => Set(ref _hotkeyGlow, value ?? ""); }

    // --- Advanced ----------------------------------------------------------------------
    private bool _loggingEnabled = true;
    public bool LoggingEnabled { get => _loggingEnabled; set => Set(ref _loggingEnabled, value); }

    private bool _debugMode;
    public bool DebugMode { get => _debugMode; set => Set(ref _debugMode, value); }

    private bool _firstRunCompleted;
    public bool FirstRunCompleted { get => _firstRunCompleted; set => Set(ref _firstRunCompleted, value); }

    public BrainboxSettings Clone() => JsonSerializer.Deserialize<BrainboxSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BrainboxSettings FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BrainboxSettings>(json, JsonOptions) ?? new BrainboxSettings();
        }
        catch (JsonException)
        {
            return new BrainboxSettings();
        }
    }

    /// <summary>Loads settings; a missing or corrupt file yields defaults (and the corrupt file is kept as .bak).</summary>
    public static BrainboxSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new BrainboxSettings();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<BrainboxSettings>(json, JsonOptions);
            if (s != null) return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return new BrainboxSettings();
    }

    /// <summary>Atomic save (write temp + replace) so a crash never leaves a half-written file.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}
