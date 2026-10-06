using System;
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
    public class ClickThroughWindow : Window
    {
        private PixelRect _physical;

        public ClickThroughWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
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
            Native.AddExStyle(Handle, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
            Native.RemoveExStyle(Handle, Native.WS_EX_APPWINDOW);
            SetCaptureExclusion(true);
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
