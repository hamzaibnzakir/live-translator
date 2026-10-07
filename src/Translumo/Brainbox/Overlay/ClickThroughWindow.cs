using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brainbox.Core.Geometry;
using Brainbox.Desktop.Interop;

namespace Brainbox.Desktop.Overlay
{
    /// <summary>
    /// Base for every Brainbox on-screen layer: transparent, top-most, never activated, invisible
    /// to the mouse (WS_EX_TRANSPARENT → clicks go to the app underneath), hidden from Alt+Tab and
    /// the taskbar, and excluded from screen capture (WDA_EXCLUDEFROMCAPTURE) so the translator can
    /// never read its own output (§16) — screenshots/recordings by other tools won't show it either.
    /// </summary>
    public enum OverlaySurface
    {
        /// <summary>WPF per-pixel-alpha layered window (UpdateLayeredWindow).</summary>
        Layered,
        /// <summary>DWM-composited redirected surface (frame extended into the client area) + WS_EX_LAYERED with constant alpha.</summary>
        Redirected,
    }

    public class ClickThroughWindow : Window
    {
        private PixelRect _physical;

        /// <summary>
        /// Surface technique for all overlay layers. "Redirected" windows are composed by DWM like
        /// normal windows, which is what SetWindowDisplayAffinity needs on some systems; "Layered"
        /// is the classic WPF transparent window. Chosen at startup (see <see cref="OverlayManager"/>).
        /// </summary>
        public static OverlaySurface Surface { get; set; } = OverlaySurface.Layered;

        /// <summary>Win32 error of the last failed SetWindowDisplayAffinity call (diagnostics).</summary>
        public int LastAffinityError { get; private set; }

        public ClickThroughWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = Surface == OverlaySurface.Layered;
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

        public IntPtr Handle { get; private set; }

        /// <summary>True when Windows accepted WDA_EXCLUDEFROMCAPTURE for this window.</summary>
        public bool ExcludedFromCapture { get; private set; }

        public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Handle = new WindowInteropHelper(this).Handle;
            if (!AllowsTransparency)
            {
                // Transparent client area composed by DWM: extend the (invisible) frame over the whole
                // window and clear WPF's render target to fully transparent.
                if (HwndSource.FromHwnd(Handle) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
                var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
            }

            Native.AddExStyle(Handle, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
            Native.RemoveExStyle(Handle, Native.WS_EX_APPWINDOW);
            if (!AllowsTransparency)
            {
                // WS_EX_TRANSPARENT only makes a top-level window click-through when it is layered.
                Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
            }

            SetCaptureExclusion(true);
            // Some systems only accept the affinity once the window has been shown/composed.
            ContentRendered += (_, _) =>
            {
                if (!ExcludedFromCapture) SetCaptureExclusion(true);
            };
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

        /// <summary>Re-asserts top-most z-order (other top-most windows like the taskbar can rise above).</summary>
        public void BringToTop()
        {
            if (Handle == IntPtr.Zero) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOOWNERZORDER);
        }
    }
}
