using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brainbox.Core.Geometry;
using Brainbox.Desktop.Interop;

namespace Brainbox.Desktop.Overlay
{
    public enum OverlaySurface
    {
        /// <summary>WPF per-pixel-alpha layered window (UpdateLayeredWindow): classic click-through via WS_EX_TRANSPARENT.</summary>
        Layered,

        /// <summary>
        /// DWM-redirected window (frame extended into the client area for transparency) whose
        /// window region is limited to what it draws. Unlike layered windows it accepts
        /// WDA_EXCLUDEFROMCAPTURE on every system we tested, so the translator can never read its
        /// own output.
        /// </summary>
        Redirected,
    }

    /// <summary>
    /// Base for every Brainbox on-screen layer: transparent, top-most, never activated or focused,
    /// hidden from Alt+Tab and the taskbar, and excluded from screen capture where Windows allows.
    /// </summary>
    public class ClickThroughWindow : Window
    {
        private const int WM_STYLECHANGING = 0x007C;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        private PixelRect _physical;
        private readonly long _pinnedExStyles;

        public ClickThroughWindow(OverlaySurface surface)
        {
            Surface = surface;
            _pinnedExStyles = Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE
                              | (surface == OverlaySurface.Layered ? Native.WS_EX_LAYERED : 0);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = surface == OverlaySurface.Layered;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Title = "Brainbox overlay";
            DpiChanged += (_, _) => Reposition();
        }

        public OverlaySurface Surface { get; }

        public IntPtr Handle { get; private set; }

        /// <summary>True when Windows accepted WDA_EXCLUDEFROMCAPTURE for this window.</summary>
        public bool ExcludedFromCapture { get; private set; }

        /// <summary>Win32 error of the last failed SetWindowDisplayAffinity call (diagnostics).</summary>
        public int LastAffinityError { get; private set; }

        public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(Handle);
            if (Surface == OverlaySurface.Redirected)
            {
                if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
                var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
                SetRegion(Array.Empty<PixelRect>()); // nothing drawn yet → nothing to click on
            }

            // WPF rewrites the extended style from its own cache (Topmost/ShowInTaskbar/Show); pin ours.
            source?.AddHook(WndProc);
            EnsureStyles();
            SetCaptureExclusion(true);
            ContentRendered += (_, _) =>
            {
                if (!ExcludedFromCapture) SetCaptureExclusion(true);
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_STYLECHANGING && wParam.ToInt64() == Native.GWL_EXSTYLE)
            {
                var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
                ss.StyleNew = (uint)((ss.StyleNew | _pinnedExStyles) & ~Native.WS_EX_APPWINDOW);
                Marshal.StructureToPtr(ss, lParam, false);
            }
            else if (msg == WM_MOUSEACTIVATE)
            {
                // Never take focus or activation from the app the user is working in.
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STYLESTRUCT
        {
            public uint StyleOld;
            public uint StyleNew;
        }

        public void EnsureStyles()
        {
            if (Handle == IntPtr.Zero) return;
            var ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & _pinnedExStyles) != _pinnedExStyles || (ex & Native.WS_EX_APPWINDOW) != 0)
            {
                Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr((ex | _pinnedExStyles) & ~Native.WS_EX_APPWINDOW));
            }
        }

        /// <summary>
        /// Limits the window to the given rectangles (window-relative physical pixels). Everywhere
        /// else the window does not exist: it is not drawn and mouse input reaches the apps below.
        /// </summary>
        public void SetRegion(IReadOnlyList<PixelRect> windowRelativeRects)
        {
            if (Handle == IntPtr.Zero) return;
            var region = Native.CreateRectRgn(0, 0, 0, 0);
            foreach (var r in windowRelativeRects)
            {
                if (r.IsEmpty) continue;
                var part = Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                Native.CombineRgn(region, region, part, Native.RGN_OR);
                Native.DeleteObject(part);
            }

            // On success the system owns the region.
            if (Native.SetWindowRgn(Handle, region, true) == 0) Native.DeleteObject(region);
        }

        /// <summary>Turns capture exclusion on/off (off only for the self-test's "screenshot with overlay").</summary>
        public bool SetCaptureExclusion(bool exclude)
        {
            if (Handle == IntPtr.Zero) return false;
            if (!exclude)
            {
                Native.SetWindowDisplayAffinity(Handle, Native.WDA_NONE);
                ExcludedFromCapture = false;
                return true;
            }

            ExcludedFromCapture = Native.SupportsExcludeFromCapture && Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            LastAffinityError = ExcludedFromCapture ? 0 : Marshal.GetLastWin32Error();
            if (ExcludedFromCapture && Native.GetWindowDisplayAffinity(Handle, out var actual) && actual != Native.WDA_EXCLUDEFROMCAPTURE)
            {
                ExcludedFromCapture = false; // pre-2004 behaviour (treated as WDA_MONITOR)
            }

            return ExcludedFromCapture;
        }

        /// <summary>Positions the window in physical virtual-desktop pixels (DPI-independent).</summary>
        public void PlaceAt(PixelRect physical)
        {
            _physical = physical;
            Reposition();
        }

        public PixelRect PhysicalBounds => _physical;

        public void Reposition()
        {
            if (Handle == IntPtr.Zero || _physical.IsEmpty) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _physical.X, _physical.Y, _physical.Width, _physical.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        /// <summary>Re-asserts top-most z-order and our window styles.</summary>
        public void BringToTop()
        {
            if (Handle == IntPtr.Zero) return;
            EnsureStyles();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOOWNERZORDER);
        }
    }
}
