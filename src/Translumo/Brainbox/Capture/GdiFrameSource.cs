using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;
using Brainbox.Desktop.Interop;

namespace Brainbox.Desktop.Capture
{
    /// <summary>
    /// Captures any rectangle of the Windows virtual desktop (all monitors, negative coordinates
    /// included) with GDI BitBlt from the composited screen. With DWM this reads the final
    /// composed image of whichever virtual desktop is currently shown, and it honours
    /// <c>WDA_EXCLUDEFROMCAPTURE</c>, so Brainbox's own overlay/glow windows are never captured.
    /// Requires the process to be per-monitor DPI aware (see app.manifest) so coordinates are physical pixels.
    /// </summary>
    public sealed class GdiFrameSource : IFrameSource
    {
        private readonly object _gate = new();
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _bits;
        private int _w;
        private int _h;
        private readonly byte[][] _buffers = new byte[2][];
        private int _next;

        /// <summary>Set by the overlay once SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) succeeded.</summary>
        public static volatile bool OverlayExcludedFromCapture;

        public bool HonoursCaptureExclusion => Native.SupportsExcludeFromCapture && OverlayExcludedFromCapture;

        public IReadOnlyList<MonitorInfo> GetMonitors() => QueryMonitors();

        public static IReadOnlyList<MonitorInfo> QueryMonitors()
        {
            return Native.EnumerateMonitors()
                .Select(m => new MonitorInfo(
                    m.Device,
                    PixelRect.FromLTRB(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom),
                    PixelRect.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom),
                    m.Scale,
                    m.Primary))
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.Bounds.X)
                .ToList();
        }

        public DesktopFrame Capture(PixelRect area)
        {
            if (area.IsEmpty) return null;
            if (!IsInputDesktopAccessible()) return null; // UAC prompt / lock screen (secure desktop)

            lock (_gate)
            {
                EnsureSurface(area.Width, area.Height);
                var screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                try
                {
                    if (!Native.BitBlt(_memDc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Native.SRCCOPY))
                    {
                        return null;
                    }

                    Native.GdiFlush();
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, screen);
                }

                var size = area.Width * area.Height * 4;
                var buf = _buffers[_next];
                if (buf == null || buf.Length != size)
                {
                    buf = new byte[size];
                    _buffers[_next] = buf;
                }

                _next ^= 1; // double buffering: the previous frame stays valid while this one is filled
                Marshal.Copy(_bits, buf, 0, size);
                return new DesktopFrame(area, buf, area.Width * 4, Environment.TickCount64, GetMonitors());
            }
        }

        private void EnsureSurface(int w, int h)
        {
            if (_memDc != IntPtr.Zero && w == _w && h == _h) return;
            ReleaseSurface();

            var screen = Native.GetDC(IntPtr.Zero);
            try
            {
                _memDc = Native.CreateCompatibleDC(screen);
                var bmi = new Native.BITMAPINFO
                {
                    bmiHeader = new Native.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                        biWidth = w,
                        biHeight = -h, // top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0, // BI_RGB
                    },
                };
                _dib = Native.CreateDIBSection(screen, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                Native.SelectObject(_memDc, _dib);
                _w = w;
                _h = h;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void ReleaseSurface()
        {
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
            _dib = _memDc = _bits = IntPtr.Zero;
            _w = _h = 0;
        }

        /// <summary>False while the secure desktop (UAC consent, Ctrl+Alt+Del, lock screen) has input.</summary>
        public static bool IsInputDesktopAccessible()
        {
            var desk = Native.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (desk == IntPtr.Zero) return false;
            Native.CloseDesktop(desk);
            return true;
        }

        public void Dispose()
        {
            lock (_gate) ReleaseSurface();
        }
    }
}
