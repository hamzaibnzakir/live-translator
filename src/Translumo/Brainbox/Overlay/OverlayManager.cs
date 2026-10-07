using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Brainbox.Core.Capture;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Overlay;
using Brainbox.Core.Settings;
using Brainbox.Desktop.Capture;

namespace Brainbox.Desktop.Overlay
{
    /// <summary>
    /// Owns the per-monitor translation layers and glow strips, rebuilds them when monitors are
    /// added/removed/re-scaled, and marshals updates from the engine thread to the UI thread.
    /// </summary>
    public sealed class OverlayManager : IDisposable
    {
        private readonly BrainboxSettings _settings;
        private readonly Dispatcher _dispatcher;
        private readonly ILog _log;
        private readonly Dictionary<string, TranslationOverlayWindow> _overlays = new();
        private readonly Dictionary<string, List<GlowEdgeWindow>> _glows = new();
        private IReadOnlyList<OverlayItem> _pending = Array.Empty<OverlayItem>();
        private bool _renderQueued;
        private bool _glowVisible;
        private bool _active;

        public OverlayManager(BrainboxSettings settings, Dispatcher dispatcher, ILog log)
        {
            _settings = settings;
            _dispatcher = dispatcher;
            _log = log;
        }

        /// <summary>True when every overlay/glow window is excluded from capture.</summary>
        public bool AllExcludedFromCapture =>
            _overlays.Count > 0 && _overlays.Values.All(o => o.ExcludedFromCapture) && _glows.Values.SelectMany(g => g).All(g => g.ExcludedFromCapture);

        public IEnumerable<IntPtr> WindowHandles =>
            _overlays.Values.Select(o => o.Handle).Concat(_glows.Values.SelectMany(g => g).Select(g => g.Handle)).ToList();

        public IReadOnlyList<TranslationOverlayWindow> OverlayWindows => _overlays.Values.ToList();

        public IReadOnlyList<GlowEdgeWindow> GlowWindows => _glows.Values.SelectMany(g => g).ToList();

        public int RenderedItemCount => _overlays.Values.Sum(o => o.ItemCount);

        /// <summary>Creates/updates/removes windows to match the monitor layout. UI thread.</summary>
        private void RecreateAll()
        {
            foreach (var o in _overlays.Values) o.Close();
            foreach (var g in _glows.Values.SelectMany(x => x)) g.Close();
            _overlays.Clear();
            _glows.Clear();
            _createdAt = DateTime.UtcNow;
            SyncMonitors(_lastMonitors);
        }

        public void SyncMonitors(IReadOnlyList<MonitorInfo> monitors)
        {
            _dispatcher.VerifyAccess();
            _lastMonitors = monitors;
            var names = monitors.Select(m => m.DeviceName).ToHashSet();

            foreach (var gone in _overlays.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _overlays[gone].Close();
                _overlays.Remove(gone);
                foreach (var g in _glows[gone]) g.Close();
                _glows.Remove(gone);
            }

            foreach (var m in monitors)
            {
                if (_overlays.TryGetValue(m.DeviceName, out var existing))
                {
                    existing.UpdateMonitor(m);
                    foreach (var g in _glows[m.DeviceName]) g.UpdateMonitor(m);
                    continue;
                }

                var overlay = new TranslationOverlayWindow(m);
                overlay.Show();
                overlay.UpdateMonitor(m);
                _overlays[m.DeviceName] = overlay;

                var strips = new List<GlowEdgeWindow>();
                foreach (GlowEdge edge in Enum.GetValues(typeof(GlowEdge)))
                {
                    var g = new GlowEdgeWindow(m, edge);
                    g.SetIntensity(_settings.GlowIntensity);
                    g.Show();
                    g.UpdateMonitor(m);
                    if (!_glowVisible) g.Hide();
                    strips.Add(g);
                }

                _glows[m.DeviceName] = strips;
            }

            GdiFrameSource.OverlayExcludedFromCapture = AllExcludedFromCapture;
            if (!GdiFrameSource.OverlayExcludedFromCapture)
            {
                var errors = string.Join(",", _overlays.Values.Select(o => o.LastAffinityError).Concat(_glows.Values.SelectMany(g => g).Select(g => g.LastAffinityError)).Distinct());
                _log.Warn($"Overlay not (yet) excluded from capture (surface={ClickThroughWindow.Surface}, Win32 errors={errors}); retrying after first render, masking + own-output filter meanwhile.");
            }
            Render(_pending);
        }

        /// <summary>Thread-safe: queues a render of the latest item set (coalesces bursts).</summary>
        public void Show(IReadOnlyList<OverlayItem> items)
        {
            lock (this)
            {
                _pending = items;
                if (_renderQueued) return;
                _renderQueued = true;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                IReadOnlyList<OverlayItem> latest;
                lock (this)
                {
                    latest = _pending;
                    _renderQueued = false;
                }

                Render(latest);
            }));
        }

        private void Render(IReadOnlyList<OverlayItem> items)
        {
            try
            {
                var byMonitor = items.GroupBy(i => i.MonitorDevice).ToDictionary(g => g.Key, g => (IReadOnlyList<OverlayItem>)g.ToList());
                foreach (var (device, window) in _overlays)
                {
                    window.Render(byMonitor.TryGetValue(device, out var list) ? list : Array.Empty<OverlayItem>(), _settings);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Overlay render failed", ex);
            }
        }

        /// <summary>Shows/hides the live glow. Thread-safe.</summary>
        public void SetGlowVisible(bool visible)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _glowVisible = visible;
                foreach (var g in _glows.Values.SelectMany(x => x))
                {
                    if (visible)
                    {
                        g.Show();
                        g.Reposition();
                        g.SetIntensity(_settings.GlowIntensity);
                    }
                    else
                    {
                        g.Hide();
                    }
                }
            }));
        }

        /// <summary>Brief, gentle brightening while text is being processed. Thread-safe.</summary>
        public void SetActivity(bool active)
        {
            if (_active == active) return;
            _active = active;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                foreach (var g in _glows.Values.SelectMany(x => x)) g.SetActive(active);
            }));
        }

        public void ApplyGlowIntensity()
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var g in _glows.Values.SelectMany(x => x)) g.SetIntensity(_settings.GlowIntensity);
            }));
        }

        private DateTime _createdAt = DateTime.UtcNow;
        private int _surfaceSwitches;
        private IReadOnlyList<MonitorInfo> _lastMonitors = Array.Empty<MonitorInfo>();

        public void BringToTop()
        {
            var excluded = AllExcludedFromCapture;

            // If Windows refuses capture exclusion for this window type, rebuild the layers once with
            // the other surface technique (some systems only accept it for DWM-redirected windows).
            if (!excluded && _surfaceSwitches < 2 && _overlays.Count > 0 && (DateTime.UtcNow - _createdAt).TotalSeconds > 2)
            {
                _surfaceSwitches++;
                var previous = ClickThroughWindow.Surface;
                ClickThroughWindow.Surface = previous == OverlaySurface.Layered ? OverlaySurface.Redirected : OverlaySurface.Layered;
                _log.Warn($"Capture exclusion refused for {previous} windows; rebuilding overlay as {ClickThroughWindow.Surface}.");
                RecreateAll();
                return;
            }

            if (excluded != GdiFrameSource.OverlayExcludedFromCapture)
            {
                GdiFrameSource.OverlayExcludedFromCapture = excluded;
                _log.Info($"Overlay capture exclusion now {(excluded ? "active" : "inactive")} (surface={ClickThroughWindow.Surface}).");
            }

            foreach (var o in _overlays.Values) o.BringToTop();
            foreach (var g in _glows.Values.SelectMany(x => x)) g.BringToTop();
        }

        /// <summary>Self-test only: lets a screenshot include the overlay.</summary>
        public void SetCaptureExclusion(bool exclude)
        {
            foreach (var o in _overlays.Values) o.SetCaptureExclusion(exclude);
            foreach (var g in _glows.Values.SelectMany(x => x)) g.SetCaptureExclusion(exclude);
            GdiFrameSource.OverlayExcludedFromCapture = exclude && AllExcludedFromCapture;
        }

        public void Dispose()
        {
            foreach (var o in _overlays.Values) o.Close();
            foreach (var g in _glows.Values.SelectMany(x => x)) g.Close();
            _overlays.Clear();
            _glows.Clear();
        }
    }
}
