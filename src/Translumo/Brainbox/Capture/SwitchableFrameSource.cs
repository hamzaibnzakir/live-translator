using System;
using System.Collections.Generic;
using Brainbox.Core.Capture;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using Brainbox.Core.Settings;

namespace Brainbox.Desktop.Capture
{
    /// <summary>
    /// Frame source whose backend (GDI / Desktop Duplication) can be swapped at runtime from
    /// Settings without recreating the engine. Falls back to GDI if the GPU path fails repeatedly.
    /// </summary>
    public sealed class SwitchableFrameSource : IFrameSource
    {
        private readonly object _gate = new();
        private readonly ILog _log;
        private IFrameSource _inner;
        private int _failures;

        public SwitchableFrameSource(CaptureBackend backend, ILog log)
        {
            _log = log;
            _inner = Create(backend);
        }

        public string BackendName { get; private set; }

        public bool HonoursCaptureExclusion
        {
            get
            {
                lock (_gate) return _inner.HonoursCaptureExclusion;
            }
        }

        public void Switch(CaptureBackend backend)
        {
            lock (_gate)
            {
                _inner.Dispose();
                _inner = Create(backend);
                _failures = 0;
            }
        }

        private IFrameSource Create(CaptureBackend backend)
        {
            if (backend == CaptureBackend.DesktopDuplication)
            {
                try
                {
                    var dx = new DxgiFrameSource(_log);
                    BackendName = "Desktop Duplication (GPU)";
                    return dx;
                }
                catch (Exception ex)
                {
                    _log.Warn("Desktop Duplication unavailable; using GDI capture.", ex);
                }
            }

            BackendName = "GDI";
            return new GdiFrameSource();
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            lock (_gate) return _inner.GetMonitors();
        }

        public DesktopFrame Capture(PixelRect area)
        {
            lock (_gate)
            {
                try
                {
                    var f = _inner.Capture(area);
                    _failures = f == null ? _failures + 1 : 0;
                    if (f == null && _failures >= 20 && _inner is DxgiFrameSource && GdiFrameSource.IsInputDesktopAccessible())
                    {
                        _log.Warn("Desktop Duplication keeps failing; switching to GDI capture.");
                        _inner.Dispose();
                        _inner = new GdiFrameSource();
                        BackendName = "GDI";
                        _failures = 0;
                    }

                    return f;
                }
                catch (Exception ex)
                {
                    _log.Warn("Capture backend error", ex);
                    return null;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) _inner.Dispose();
        }
    }
}
