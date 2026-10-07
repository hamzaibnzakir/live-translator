using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Core.Overlay;
using Brainbox.Core.Pipeline;
using Brainbox.Core.Settings;
using Brainbox.Core.Text;
using Brainbox.Core.Translation;
using Brainbox.Desktop.Capture;
using Brainbox.Desktop.Interop;
using Brainbox.Desktop.Ocr;
using Brainbox.Desktop.Overlay;
using Brainbox.Desktop.Shell;
using Translumo;

namespace Brainbox.Desktop.SelfTest
{
    /// <summary>
    /// Real-screen end-to-end test (§22), run with <c>--selftest [--out dir] [--interactive]</c>.
    /// It launches the real Brainbox engine (GDI/DXGI capture → Windows OCR → filters → cache →
    /// OpenAI-compatible local AI → overlay/glow windows), puts foreign text on the real desktop,
    /// and checks from the outside — by capturing the screen, OCR'ing it, clicking through the
    /// overlay with synthetic input — that everything behaves as specified. A fake LM Studio
    /// server on 127.0.0.1 makes translations deterministic and lets the test "close LM Studio".
    /// Results: report.json, report.md and screenshots in the output folder; exit code 0 = all pass.
    /// </summary>
    public static class SelfTestRunner
    {
        private const string FrenchA = "Bienvenue dans le monde de la traduction";
        private const string FrenchAEn = "Welcome to the world of translation";
        private const string FrenchB = "Le train arrive dans cinq minutes";
        private const string FrenchBEn = "The train arrives in five minutes";
        private const string FrenchC = "Merci beaucoup pour votre patience";
        private const string FrenchCEn = "Thank you very much for your patience";
        private const string Japanese = "東京の天気は晴れです";
        private const string JapaneseEn = "The weather in Tokyo is sunny";
        private const string Spanish = "¿Dónde está la estación de tren?";
        private const string SpanishEn = "Where is the train station?";
        private const string English = "This sentence is already written in English";

        private sealed record Result(string Name, string Status, string Details, double Ms);

        public static async Task RunAsync(App app, string[] args, Func<ITranslationProvider> translumo)
        {
            var outDir = ArgValue(args, "--out") ?? Path.Combine(Path.GetTempPath(), "brainbox-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outDir);
            Environment.SetEnvironmentVariable("BRAINBOX_DATA_DIR", Path.Combine(outDir, "data"));
            var interactive = args.Any(a => a == "--interactive");
            if (string.Equals(ArgValue(args, "--engine"), "ollama", StringComparison.OrdinalIgnoreCase))
            {
                await RealEngineRunAsync(app, outDir, ArgValue(args, "--model") ?? "", ArgValue(args, "--endpoint") ?? "http://localhost:11434/v1");
                return;
            }

            var results = new List<Result>();
            var metrics = new Dictionary<string, object>();
            var log = new StringBuilder();
            int exitCode;

            void Log(string s)
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
                log.AppendLine(line);
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(outDir, "selftest.log"), line + Environment.NewLine);
            }

            void Record(string name, bool? pass, string details, double ms = 0)
            {
                var status = pass == null ? "SKIP" : pass.Value ? "PASS" : "FAIL";
                results.Add(new Result(name, status, details, ms));
                Log($"[{status}] {name} — {details}");
            }

            FakeLocalAiServer server = null;
            BrainboxController controller = null;
            Window testWindow = null;
            try
            {
                Log($"Brainbox self-test. OS {Environment.OSVersion.Version}, 64-bit={Environment.Is64BitProcess}, out={outDir}");
                var ocrProbe = new WindowsOcrEngineAdapter();
                var ocrTags = ocrProbe.InstalledLanguageTags;
                var hasJapanese = ocrTags.Any(t => t.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
                Log("OCR recognizers: " + string.Join(", ", ocrTags));
                var monitors = GdiFrameSource.QueryMonitors();
                foreach (var m in monitors) Log($"Monitor {m.DeviceName} {m.Bounds} scale {m.DpiScale:0.##} primary={m.IsPrimary}");
                metrics["os"] = Environment.OSVersion.VersionString;
                metrics["ocrLanguages"] = ocrTags;
                metrics["monitors"] = monitors.Select(m => new { m.DeviceName, Bounds = m.Bounds.ToString(), m.DpiScale, m.IsPrimary }).ToList();

                // ---- environment probes (diagnostics, not pass/fail) ------------------------------
                var probePrimary = monitors.First(m => m.IsPrimary);
                foreach (var probe in await SurfaceProbe.ProbeSurfacesAsync(probePrimary))
                {
                    results.Add(new Result("Probe: overlay surface " + probe.Surface, "INFO", probe.ToString(), 0));
                    Log("Probe " + probe);
                }

                var dxProbe = await SurfaceProbe.ProbeDuplicationAsync(probePrimary, Brainbox.Core.Diagnostics.NullLog.Instance);
                results.Add(new Result("Probe: Desktop Duplication", "INFO", dxProbe, 0));
                Log("Probe " + dxProbe);

                // ---- fake LM Studio --------------------------------------------------------
                server = new FakeLocalAiServer(new Dictionary<string, string>
                {
                    [FrenchA] = FrenchAEn,
                    [FrenchB] = FrenchBEn,
                    [FrenchC] = FrenchCEn,
                    [Japanese] = JapaneseEn,
                    [Spanish] = SpanishEn,
                });
                server.Start();
                Log("Fake local AI server at " + server.BaseUrl);

                // ---- isolated settings -------------------------------------------------------
                var settings = new BrainboxSettings
                {
                    Engine = TranslationEngineKind.CustomOpenAi,
                    CustomEndpoint = server.BaseUrl,
                    Model = "",
                    PerformanceMode = PerformanceMode.Performance,
                    GlowEnabled = true,
                    StartWithWindows = false,
                    FirstRunCompleted = true,
                    HotkeysEnabled = true,
                    RequestTimeoutSeconds = 10,
                    OverlayBackground = OverlayBackground.Dark,
                };
                settings.Save(BrainboxPaths.Settings);

                // ---- test window with foreign + English text ------------------------------------
                var primary = monitors.First(m => m.IsPrimary);
                var (window, french, japanese, english, clicks) = CreateTestWindow(hasJapanese);
                testWindow = window;
                testWindow.Show();
                testWindow.Activate();
                await PlaceWindowAsync(testWindow, primary.WorkArea.X + 120, primary.WorkArea.Y + 120);
                await Task.Delay(500);

                // ---- start the real engine ------------------------------------------------------
                var sw = Stopwatch.StartNew();
                controller = new BrainboxController(app.Dispatcher, translumo);
                controller.Start(fromAutostart: false);
                var tray = new TrayIcon(controller, () => { }, () => { }, () => { }, () => { });
                Record("System tray icon created", true, "Tray menu with Live/Target/Engine/Mode/Glow/Pause/Settings/Restart/Exit");
                Log("Engine started; capture backend " + controller.Frames.BackendName);

                // T1: automatic detection + translation + placement
                var found = await WaitForAsync(() => FindItem(controller, FrenchAEn) != null, 40_000);
                var latency = sw.Elapsed.TotalMilliseconds;
                var item = FindItem(controller, FrenchAEn);
                var frenchRect = ScreenRect(french);
                Record("T1 Foreign text detected & translated automatically", found,
                    found ? $"'{FrenchA}' → '{item.Text}' in {latency:0} ms with no user action" : "no overlay for the French text (OCR or translation failed)", latency);
                if (found)
                {
                    var overlaps = item.Box.Intersect(frenchRect).Area > 0.5 * frenchRect.Area;
                    Record("T1b Translation placed over the original text", overlaps, $"source {frenchRect}, overlay {item.Box}");
                    await WaitForAsync(() => controller.Overlay.RenderedItemCount > 0, 3000);
                    Record("T1c Overlay actually rendered on screen", controller.Overlay.RenderedItemCount > 0, $"{controller.Overlay.RenderedItemCount} item(s) on overlay windows");
                }

                if (hasJapanese)
                {
                    var jp = await WaitForAsync(() => FindItem(controller, JapaneseEn) != null, 20_000);
                    Record("T1d Japanese detected & translated", jp, jp ? $"'{Japanese}' → '{JapaneseEn}'" : "Japanese line not translated");
                }
                else
                {
                    Record("T1d Japanese detected & translated", null, "ja-JP Windows OCR recognizer not installed on this machine");
                }

                // T2: English is not translated
                await Task.Delay(1500);
                var englishSent = server.TranslatedSegments.Any(s => s.Contains("already written", StringComparison.OrdinalIgnoreCase));
                var englishOverlay = controller.Watcher.CurrentOverlay.Any(o => o.SourceText.Contains("already written", StringComparison.OrdinalIgnoreCase));
                Record("T2 English text is ignored (not English→English)", !englishSent && !englishOverlay, $"sent={englishSent}, overlay={englishOverlay}");

                // Screenshots for humans (overlay is invisible to capture, so take one with it allowed).
                await SaveScreenshotsAsync(controller, outDir, Log);

                // T3/T11: overlay excluded from capture and never OCR'd
                var excluded = controller.Overlay.AllExcludedFromCapture;
                Record("T11a Translation overlay excluded from screen capture", excluded,
                    excluded ? "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) accepted for all layers" : "exclusion unavailable — masking + own-output filter in use");
                if (found)
                {
                    var frame = new GdiFrameSource().Capture(frenchRect.Inflate(40, 20));
                    var read = await new WindowsOcrEngineAdapter().RecognizeAsync(frame, frame.Bounds, new OcrRequest(new[] { "en-US" }, false, 1.25), CancellationToken.None);
                    var text = string.Join(" | ", read.Select(r => r.Text));
                    var seesOriginal = TextNormalizer.Similarity(text, FrenchA) > 0.6 || text.Contains("Bienvenue", StringComparison.OrdinalIgnoreCase);
                    var seesOverlay = text.Contains("Welcome", StringComparison.OrdinalIgnoreCase);
                    Record("T11b Capture under the overlay shows the original, not the translation", seesOriginal && !seesOverlay, $"OCR of captured area: '{text}'");
                }

                var before = server.TranslatedSegments.Count;
                var ocrBefore = controller.Watcher.Metrics.OcrCalls;
                await Task.Delay(15_000);
                var ownFed = server.TranslatedSegments.Any(s => s.Contains("Welcome", StringComparison.OrdinalIgnoreCase) || s.StartsWith("[EN]", StringComparison.Ordinal) || s.Contains("weather in Tokyo", StringComparison.OrdinalIgnoreCase));
                Record("T11c Translator never translates its own overlay", !ownFed, $"segments sent so far: {string.Join(" / ", server.TranslatedSegments)}");

                // T7: unchanged text is not repeatedly translated
                var after = server.TranslatedSegments.Count;
                var ocrAfter = controller.Watcher.Metrics.OcrCalls;
                Record("T7 Unchanged on-screen text is not re-translated", after == before, $"translation requests during 15 s idle: {after - before}; OCR passes: {ocrAfter - ocrBefore}");
                metrics["idle"] = await MeasureAsync(controller, 10_000);

                // T10: click-through + keyboard focus untouched
                if (found)
                {
                    var target = ScreenRect(french);
                    var hitPoint = new Native.POINT { X = (int)target.CenterX, Y = (int)target.CenterY };
                    var overlayCovers = controller.Watcher.CurrentOverlay.Any(o => o.Box.Contains(hitPoint.X, hitPoint.Y));
                    var hit = Native.WindowFromPoint(hitPoint);
                    var title = new StringBuilder(256);
                    Native.GetWindowText(hit, title, 256);
                    Log($"WindowFromPoint({hitPoint.X},{hitPoint.Y}) = 0x{hit.ToInt64():X} '{title}' exStyle=0x{Native.GetWindowLongPtr(hit, Native.GWL_EXSTYLE).ToInt64():X}; overlay handles: {string.Join(",", controller.Overlay.WindowHandles.Select(h => "0x" + h.ToInt64().ToString("X") + " ex=0x" + Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64().ToString("X")))}");
                    var clicksBefore = clicks();
                    // Like a user: the pointer arrives over the translation, then clicks.
                    Native.SetCursorPos(hitPoint.X, hitPoint.Y);
                    await Task.Delay(400);
                    var peeked = controller.Overlay.OverlayWindows.Any(w => w.PeekedItemId >= 0);
                    Log($"Pointer over translation → peeked: {peeked}");
                    Click(hitPoint.X, hitPoint.Y);
                    await Task.Delay(600);
                    var received = clicks() > clicksBefore;
                    var fg = Native.GetForegroundWindow();
                    var stoleFocus = controller.Overlay.WindowHandles.Contains(fg);
                    Record("T10 Overlay does not block mouse input", received && !stoleFocus,
                        $"click at ({hitPoint.X},{hitPoint.Y}) under overlay={overlayCovers} reached the app: {received}; translation peeked away under pointer: {peeked}; overlay took focus: {stoleFocus}");
                }

                // T6: window moves → overlay follows (and no new translation request)
                if (found)
                {
                    var requests = server.TranslatedSegments.Count(x => x.Contains("Bienvenue", StringComparison.OrdinalIgnoreCase));
                    var oldBox = FindItem(controller, FrenchAEn).Box;
                    await PlaceWindowAsync(testWindow, primary.WorkArea.X + 420, primary.WorkArea.Y + 260);
                    var newRect = ScreenRect(french);
                    var follow = await WaitForAsync(() =>
                    {
                        var it = FindItem(controller, FrenchAEn);
                        return it != null && it.Box.Intersect(newRect).Area > 0.5 * newRect.Area;
                    }, 15_000);
                    var moved = FindItem(controller, FrenchAEn);
                    Record("T6 Overlay follows moved window", follow, $"overlay {oldBox} → {moved?.Box}, text now at {newRect}");
                    var resent = server.TranslatedSegments.Count(x => x.Contains("Bienvenue", StringComparison.OrdinalIgnoreCase)) - requests;
                    Record("T6b Moved text served from cache (no new request)", resent == 0, $"times the moved French line was re-sent for translation: {resent}");
                }

                // T8b: changed text → new translation, removed text → overlay disappears
                french.Text = FrenchB;
                var changed = await WaitForAsync(() => FindItem(controller, FrenchBEn) != null && FindItem(controller, FrenchAEn) == null, 20_000);
                Record("Text change updates the translation", changed, changed ? $"now '{FrenchBEn}'" : "overlay did not update");
                french.Text = "";
                var gone = await WaitForAsync(() => FindItem(controller, FrenchBEn) == null, 15_000);
                Record("Overlay disappears when the source text disappears", gone, gone ? "removed" : "stale overlay remained");

                // T8: LM Studio closed → app survives, status shows it, recovers
                server.Stop();
                french.Text = FrenchC;
                await Task.Delay(9_000);
                var alive = controller.Watcher.IsRunning;
                var degraded = controller.Status.State == WatcherState.Degraded;
                Record("T8 Local AI closed: app keeps running and reports it", alive && degraded, $"running={alive}, state={controller.Status.State}, message='{controller.Status.Message}'");
                server.Start();
                var recovered = await WaitForAsync(() => FindItem(controller, FrenchCEn) != null, 70_000);
                Record("T8b Recovers automatically when the local AI is back", recovered, recovered ? $"state={controller.Status.State}" : "no translation after restart");

                // Fullscreen-style window: overlay must stay on top
                testWindow.WindowState = WindowState.Maximized;
                testWindow.Topmost = true;
                await Task.Delay(500);
                testWindow.Topmost = false;
                var fsOk = await WaitForAsync(() => FindItem(controller, FrenchCEn) != null, 20_000);
                Record("Survives maximized/fullscreen window changes", fsOk && controller.Watcher.IsRunning, $"overlay present after maximize: {fsOk}");
                testWindow.WindowState = WindowState.Normal;
                await Task.Delay(500);

                // Hotkeys: pause / glow / toggle
                var pausedBefore = controller.IsPaused;
                SendChord(Native.MOD_CONTROL | Native.MOD_ALT, 0x50); // Ctrl+Alt+P
                var paused = await WaitForAsync(() => controller.IsPaused != pausedBefore, 3000);
                var overlayHidden = controller.Watcher.CurrentOverlay.Count == 0;
                SendChord(Native.MOD_CONTROL | Native.MOD_ALT, 0x50);
                var resumed = await WaitForAsync(() => controller.IsPaused == pausedBefore, 3000);
                Record("Hotkey Ctrl+Alt+P pauses and resumes", paused && resumed && overlayHidden, $"paused={paused}, overlay hidden while paused={overlayHidden}, resumed={resumed}");
                var glowBefore = controller.Settings.GlowEnabled;
                SendChord(Native.MOD_CONTROL | Native.MOD_ALT, 0x47); // Ctrl+Alt+G
                var glowToggled = await WaitForAsync(() => controller.Settings.GlowEnabled != glowBefore, 3000);
                if (glowToggled) controller.Settings.GlowEnabled = glowBefore;
                Record("Hotkey Ctrl+Alt+G toggles the glow", glowToggled, $"glow {glowBefore} → {!glowBefore}");
                SendChord(Native.MOD_CONTROL | Native.MOD_ALT, 0x42); // Ctrl+Alt+B
                var liveToggled = await WaitForAsync(() => !controller.Settings.LiveTranslationEnabled, 3000);
                var offClears = await WaitForAsync(() => controller.Watcher.CurrentOverlay.Count == 0, 3000);
                controller.Settings.LiveTranslationEnabled = true;
                Record("Hotkey Ctrl+Alt+B toggles live translation", liveToggled && offClears, $"turned off={liveToggled}, overlay cleared={offClears}");
                if (!string.IsNullOrEmpty(controller.HotkeyErrors)) Log("Hotkey registration notes: " + controller.HotkeyErrors);

                // Glow
                var glowWindows = controller.Overlay.GlowWindows;
                await Task.Delay(800);
                var glowShown = glowWindows.Count > 0 && glowWindows.All(g => g.IsVisible);
                Record("Glow indicator visible on every monitor edge", glowShown,
                    $"{glowWindows.Count} edge strips, visible={glowShown}, excluded from capture={controller.Overlay.GlowExcludedFromCapture} (masked from detection otherwise)");

                // Startup registration (a real reboot cannot be done here)
                var startupOk = StartupManager.Apply(true) && StartupManager.IsEnabled();
                StartupManager.Apply(false);
                Record("Start-with-Windows registration", startupOk, "HKCU Run entry created with --autostart and removed again (reboot not performed in this test)");

                // Virtual desktops
                await VirtualDesktopTestAsync(controller, Record, Log, primary);

                // Multiple monitors
                Record("Multiple monitors", monitors.Count > 1 ? true : null,
                    monitors.Count > 1 ? $"{monitors.Count} monitors monitored: {controller.ScreenText}" : "only one display attached here (multi-monitor coordinate logic covered by unit tests)");

                // GPU capture backend
                controller.Settings.CaptureBackend = CaptureBackend.DesktopDuplication;
                await Task.Delay(1500);
                Log("Capture backend now: " + controller.Frames.BackendName);
                french.Text = FrenchA;
                var dxOk = await WaitForAsync(() => FindItem(controller, FrenchAEn) != null, 25_000);
                Record("Desktop Duplication (GPU) capture backend", dxOk, $"backend={controller.Frames.BackendName}");
                controller.Settings.CaptureBackend = CaptureBackend.Auto;

                // Busy metrics
                metrics["active"] = await MeasureAsync(controller, 10_000, () =>
                {
                    french.Text = french.Text == FrenchA ? FrenchB : FrenchA;
                });
                metrics["engine"] = new
                {
                    controller.Watcher.Metrics.AvgTickIntervalMs,
                    controller.Watcher.Metrics.AvgCaptureMs,
                    controller.Watcher.Metrics.AvgDetectMs,
                    controller.Watcher.Metrics.AvgOcrMs,
                    controller.Watcher.Metrics.MaxOcrMs,
                    controller.Watcher.Metrics.OcrCalls,
                    controller.Watcher.Metrics.AvgTranslationMs,
                    controller.Watcher.Metrics.TranslationRequests,
                    controller.Watcher.Metrics.CacheHits,
                    controller.Watcher.Metrics.CacheMisses,
                    controller.Watcher.Metrics.AvgEndToEndMs,
                };
                Log("Engine metrics: " + controller.Watcher.Metrics);

                tray.Dispose();
                Record("Application stayed alive through all scenarios", controller.Watcher.IsRunning, "watcher loop running");
            }
            catch (Exception ex)
            {
                Record("Self-test harness", false, "crashed: " + ex);
            }
            finally
            {
                exitCode = results.Any(r => r.Status == "FAIL") ? 1 : 0;
                WriteReport(outDir, results, metrics);
                Log($"Done: {results.Count(r => r.Status == "PASS")} passed, {results.Count(r => r.Status == "FAIL")} failed, {results.Count(r => r.Status == "SKIP")} skipped.");
                if (interactive)
                {
                    MessageBox.Show(File.ReadAllText(Path.Combine(outDir, "report.md")), "Brainbox self-test", MessageBoxButton.OK,
                        exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }

                try
                {
                    controller?.Dispose();
                }
                catch (Exception ex)
                {
                    Log("Dispose failed: " + ex.Message);
                }

                server?.Dispose();
                testWindow?.Close();
            }

            app.Shutdown(exitCode);
        }

        // ------------------------------------------------------------------------------------

        private static async Task VirtualDesktopTestAsync(BrainboxController controller, Action<string, bool?, string, double> record, Action<string> log, MonitorInfo primary)
        {
            if (controller.Keeper == null || !controller.Keeper.IsSupported)
            {
                record("Virtual desktop switching", null, "IVirtualDesktopManager not available on this Windows edition", 0);
                return;
            }

            var switchesBefore = controller.Keeper.SwitchCount;
            // Win+Ctrl+D creates and switches to a new virtual desktop.
            SendWinCtrl(0x44);
            await Task.Delay(2500);

            var (w2, _, _, _, _) = CreateTestWindow(false, Spanish);
            w2.Show();
            w2.Activate();
            await PlaceWindowAsync(w2, primary.WorkArea.X + 200, primary.WorkArea.Y + 200);
            controller.Keeper.Check();
            var translated = await WaitForAsync(() => FindItem(controller, SpanishEn) != null, 30_000);
            var onCurrent = controller.Overlay.WindowHandles.All(IsOnCurrentDesktop);
            var detected = controller.Keeper.SwitchCount > switchesBefore;
            log($"Virtual desktop: switch detected={detected}, overlay on current desktop={onCurrent}, translated={translated}");
            w2.Close();

            // Win+Ctrl+F4 closes the new desktop and returns to the original one.
            SendWinCtrl(0x73);
            await Task.Delay(2500);
            controller.Keeper.Check();
            var backOnCurrent = controller.Overlay.WindowHandles.All(IsOnCurrentDesktop);
            var stillWorks = await WaitForAsync(() => controller.Watcher.CurrentOverlay.Count > 0, 25_000);
            record("Virtual desktop switching keeps translation active", detected && translated && onCurrent && backOnCurrent && stillWorks,
                $"switch detected={detected}, new desktop translated={translated}, overlay followed={onCurrent}, back on desktop 1 overlay ok={backOnCurrent}, translation after return={stillWorks}", 0);
        }

        private static bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            try
            {
                var vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
                return vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) == 0 && on != 0;
            }
            catch
            {
                return true;
            }
        }

        private static (Window Window, TextBlock French, TextBlock Japanese, TextBlock English, Func<int> Clicks)
            CreateTestWindow(bool withJapanese, string mainText = FrenchA)
        {
            var clicks = 0;
            var stack = new StackPanel { Margin = new Thickness(24) };
            TextBlock Line(string text, double size, string font) => new()
            {
                Text = text,
                FontSize = size,
                FontFamily = new FontFamily(font),
                Foreground = Brushes.Black,
                Background = Brushes.White, // hit-testable everywhere (click-through test)
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 24),
            };
            var title = new TextBlock { Text = "Brainbox self-test page", FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 18) };
            var french = Line(mainText, 26, "Segoe UI");
            var japanese = Line(withJapanese ? Japanese : "", 28, "Yu Gothic UI, Meiryo UI, MS Gothic");
            var english = Line(English, 22, "Segoe UI");
            stack.Children.Add(title);
            stack.Children.Add(french);
            stack.Children.Add(japanese);
            stack.Children.Add(english);
            var w = new Window
            {
                Title = "Brainbox Self-Test",
                Width = 760,
                Height = 400,
                Background = Brushes.White,
                Content = stack,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100,
            };
            // Clicking where the French translation is drawn must reach this TextBlock (click-through).
            french.MouseLeftButtonDown += (_, _) => clicks++;
            return (w, french, japanese, english, () => clicks);
        }

        private static PixelRect ScreenRect(FrameworkElement e)
        {
            var tl = e.PointToScreen(new Point(0, 0));
            var br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
            return PixelRect.FromLTRB((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        }

        private static async Task PlaceWindowAsync(Window w, int xPx, int yPx)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            Native.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            w.Activate();
            await Task.Delay(400);
        }

        private static OverlayItem FindItem(BrainboxController c, string translation) =>
            c.Watcher.CurrentOverlay.FirstOrDefault(o => string.Equals(o.Text, translation, StringComparison.OrdinalIgnoreCase));

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(200);
            }

            return condition();
        }

        private static async Task<object> MeasureAsync(BrainboxController c, int ms, Action every2s = null)
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            var gpu = new GpuSampler(proc.Id);
            var gpuSamples = new List<double>();
            var ticks0 = c.Watcher.Metrics.Ticks;
            while (sw.ElapsedMilliseconds < ms)
            {
                await Task.Delay(2000);
                every2s?.Invoke();
                var g = gpu.Sample();
                if (g >= 0) gpuSamples.Add(g);
            }

            proc.Refresh();
            var cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            return new
            {
                cpuPercentOfMachine = Math.Round(cpuPct, 2),
                workingSetMB = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                privateMB = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1),
                gpuPercent = gpuSamples.Count > 0 ? Math.Round(gpuSamples.Average(), 2) : (double?)null,
                ticksPerSecond = Math.Round((c.Watcher.Metrics.Ticks - ticks0) / sw.Elapsed.TotalSeconds, 2),
                avgDetectionIntervalMs = Math.Round(c.Watcher.Metrics.AvgTickIntervalMs, 1),
                avgOcrMs = Math.Round(c.Watcher.Metrics.AvgOcrMs, 1),
                avgTranslationMs = Math.Round(c.Watcher.Metrics.AvgTranslationMs, 1),
            };
        }

        private static async Task SaveScreenshotsAsync(BrainboxController c, string dir, Action<string> log)
        {
            try
            {
                var monitors = GdiFrameSource.QueryMonitors();
                var area = MonitorInfo.VirtualBounds(monitors);
                var gdi = new GdiFrameSource();
                var plain = gdi.Capture(area);
                if (plain != null) File.WriteAllBytes(Path.Combine(dir, "screen-capture-as-brainbox-sees-it.png"), ImageOps.EncodePng(plain.Bgra, plain.Width, plain.Height));
                c.Watcher.HoldCapture = true; // don't let the engine see the overlay while exclusion is off
                await Task.Delay(600);
                c.Overlay.SetCaptureExclusion(false);
                await Task.Delay(400);
                var withOverlay = gdi.Capture(area);
                if (withOverlay != null) File.WriteAllBytes(Path.Combine(dir, "screen-with-overlay-as-user-sees-it.png"), ImageOps.EncodePng(withOverlay.Bgra, withOverlay.Width, withOverlay.Height));
            }
            catch (Exception ex)
            {
                log("Screenshot failed: " + ex.Message);
            }
            finally
            {
                c.Overlay.SetCaptureExclusion(true);
                await Task.Delay(300);
                c.Watcher.HoldCapture = false;
            }
        }

        private static void Click(int x, int y)
        {
            Native.SetCursorPos(x, y);
            Thread.Sleep(60);
            var inputs = new[]
            {
                new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTDOWN } },
                new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTUP } },
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        }

        private static void SendChord(uint mods, byte vk)
        {
            const uint up = 0x0002;
            if ((mods & Native.MOD_CONTROL) != 0) Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            if ((mods & Native.MOD_ALT) != 0) Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, up, UIntPtr.Zero);
            if ((mods & Native.MOD_ALT) != 0) Native.keybd_event(0x12, 0, up, UIntPtr.Zero);
            if ((mods & Native.MOD_CONTROL) != 0) Native.keybd_event(0x11, 0, up, UIntPtr.Zero);
        }

        private static void SendWinCtrl(byte vk)
        {
            const uint up = 0x0002;
            Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero); // LWin
            Native.keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, up, UIntPtr.Zero);
            Native.keybd_event(0x11, 0, up, UIntPtr.Zero);
            Native.keybd_event(0x5B, 0, up, UIntPtr.Zero);
        }

        private static string ArgValue(string[] args, string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>
        /// Same pipeline against a REAL local LLM (Ollama, OpenAI-compatible endpoint): no
        /// dictionary, the model decides the wording, so checks are semantic.
        /// </summary>
        private static async Task RealEngineRunAsync(App app, string outDir, string model, string endpoint)
        {
            var results = new List<Result>();
            var metrics = new Dictionary<string, object>();
            void Log(string s)
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(outDir, "selftest.log"), line + Environment.NewLine);
            }

            void Record(string name, bool? pass, string details)
            {
                var status = pass == null ? "SKIP" : pass.Value ? "PASS" : "FAIL";
                results.Add(new Result(name, status, details, 0));
                Log($"[{status}] {name} — {details}");
            }

            BrainboxController controller = null;
            Window w = null;
            try
            {
                new BrainboxSettings
                {
                    Engine = TranslationEngineKind.Ollama,
                    OllamaEndpoint = endpoint,
                    Model = model,
                    PerformanceMode = PerformanceMode.Balanced,
                    StartWithWindows = false,
                    FirstRunCompleted = true,
                    RequestTimeoutSeconds = 120,
                    GlowEnabled = true,
                    DebugMode = true,
                }.Save(BrainboxPaths.Settings);

                var monitors = GdiFrameSource.QueryMonitors();
                var primary = monitors.First(m => m.IsPrimary);
                var (window, french, japanese, _, _) = CreateTestWindow(new WindowsOcrEngineAdapter().InstalledLanguageTags.Any(t => t.StartsWith("ja", StringComparison.OrdinalIgnoreCase)));
                w = window;
                w.Show();
                await PlaceWindowAsync(w, primary.WorkArea.X + 120, primary.WorkArea.Y + 120);

                var sw = Stopwatch.StartNew();
                controller = new BrainboxController(app.Dispatcher);
                controller.Start(false);
                Log($"Real engine: Ollama at {endpoint}, model '{(string.IsNullOrEmpty(model) ? "(auto)" : model)}'");

                OverlayItem BySource(string src) => controller.Watcher.CurrentOverlay.FirstOrDefault(o => TextNormalizer.Similarity(o.SourceText, src) > 0.8);
                var ok = await WaitForAsync(() => BySource(FrenchA) != null, 240_000);
                var fr = BySource(FrenchA);
                var text = fr?.Text ?? "";
                var english = text.Contains("welcome", StringComparison.OrdinalIgnoreCase) || text.Contains("world", StringComparison.OrdinalIgnoreCase) || text.Contains("translation", StringComparison.OrdinalIgnoreCase);
                Record("Real local LLM (Ollama) translates on-screen French", ok && english, ok ? $"'{FrenchA}' → '{text}' after {sw.Elapsed.TotalSeconds:0.0}s (includes model load)" : $"no translation; status '{controller.Status.Message}'");
                if (japanese.Text.Length > 0)
                {
                    var jp = await WaitForAsync(() => BySource(Japanese) != null, 120_000);
                    Record("Real local LLM translates on-screen Japanese", jp, jp ? $"'{Japanese}' → '{BySource(Japanese).Text}'" : "no translation");
                }

                var before = controller.Watcher.Metrics.TranslationRequests;
                await Task.Delay(15_000);
                Record("Real engine: unchanged text not re-translated", controller.Watcher.Metrics.TranslationRequests == before, $"requests during 15 s idle: {controller.Watcher.Metrics.TranslationRequests - before}");

                french.Text = FrenchB;
                var sw2 = Stopwatch.StartNew();
                var changed = await WaitForAsync(() => BySource(FrenchB) != null, 120_000);
                Record("Real engine: new subtitle-style line translated with context", changed, changed ? $"'{FrenchB}' → '{BySource(FrenchB).Text}' in {sw2.ElapsedMilliseconds} ms" : "no translation");
                metrics["engine"] = controller.Watcher.Metrics.ToString();
                metrics["model"] = model;
            }
            catch (Exception ex)
            {
                Record("Real-engine harness", false, "crashed: " + ex);
            }
            finally
            {
                WriteReport(outDir, results, metrics);
                try
                {
                    controller?.Dispose();
                }
                catch
                {
                }

                w?.Close();
            }

            app.Shutdown(results.Any(r => r.Status == "FAIL") ? 1 : 0);
        }

        private static void WriteReport(string dir, List<Result> results, Dictionary<string, object> metrics)
        {
            var json = JsonSerializer.Serialize(new { results, metrics }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "report.json"), json);

            var md = new StringBuilder();
            md.AppendLine("# Brainbox Live Translator — real-screen self-test");
            md.AppendLine();
            md.AppendLine($"Passed **{results.Count(r => r.Status == "PASS")}**, failed **{results.Count(r => r.Status == "FAIL")}**, skipped **{results.Count(r => r.Status == "SKIP")}**.");
            md.AppendLine();
            md.AppendLine("| Result | Check | Details |");
            md.AppendLine("|---|---|---|");
            foreach (var r in results) md.AppendLine($"| {r.Status} | {r.Name} | {r.Details.Replace("|", "/").Replace("\n", " ")} |");
            md.AppendLine();
            md.AppendLine("## Metrics");
            md.AppendLine("```json");
            md.AppendLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            md.AppendLine("```");
            File.WriteAllText(Path.Combine(dir, "report.md"), md.ToString());
        }

        /// <summary>Best-effort GPU utilisation of this process from the "GPU Engine" performance counters.</summary>
        private sealed class GpuSampler
        {
            private readonly List<PerformanceCounter> _counters = new();

            public GpuSampler(int pid)
            {
                try
                {
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    foreach (var name in cat.GetInstanceNames().Where(n => n.Contains($"pid_{pid}_", StringComparison.Ordinal)))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                        pc.NextValue();
                        _counters.Add(pc);
                    }
                }
                catch
                {
                    // GPU counters unavailable (no WDDM 2.x GPU / VM)
                }
            }

            public double Sample()
            {
                if (_counters.Count == 0) return -1;
                try
                {
                    return _counters.Sum(c => c.NextValue());
                }
                catch
                {
                    return -1;
                }
            }
        }
    }
}
