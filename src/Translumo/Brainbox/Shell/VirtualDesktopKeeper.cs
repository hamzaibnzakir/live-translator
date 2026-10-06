using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Brainbox.Core.Diagnostics;
using Brainbox.Desktop.Interop;

namespace Brainbox.Desktop.Shell
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    internal class VirtualDesktopManagerClass
    {
    }

    /// <summary>
    /// Keeps Brainbox's overlay and glow on whichever Windows virtual desktop is active (§2, §15).
    /// Uses the documented IVirtualDesktopManager API: when the user switches desktops (detected
    /// via foreground changes plus a light timer), any Brainbox window that is not on the current
    /// desktop is moved there (or re-shown, which also lands it on the current desktop), and the
    /// watcher is told to re-scan because the whole screen content changed.
    /// </summary>
    public sealed class VirtualDesktopKeeper : IDisposable
    {
        private readonly Func<IEnumerable<IntPtr>> _windows;
        private readonly Action _onDesktopSwitched;
        private readonly ILog _log;
        private readonly DispatcherTimer _timer;
        private readonly Native.WinEventDelegate _hookProc;
        private readonly IVirtualDesktopManager _vdm;
        private IntPtr _hook;
        private Guid _lastDesktop = Guid.Empty;

        public VirtualDesktopKeeper(Func<IEnumerable<IntPtr>> windows, Action onDesktopSwitched, ILog log)
        {
            _windows = windows;
            _onDesktopSwitched = onDesktopSwitched;
            _log = log;
            try
            {
                _vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
            }
            catch (Exception ex)
            {
                _log.Warn("Virtual desktop manager unavailable; overlay stays on its desktop.", ex);
            }

            _hookProc = OnForegroundChanged;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += (_, _) => Check();
            _timer.Start();
        }

        public bool Enabled { get; set; } = true;

        public bool IsSupported => _vdm != null;

        /// <summary>Number of times a desktop switch was detected (diagnostics / self-test).</summary>
        public int SwitchCount { get; private set; }

        private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Check();

        public void Check()
        {
            if (!Enabled || _vdm == null) return;
            try
            {
                var fg = Native.GetForegroundWindow();
                Guid current = Guid.Empty;
                if (fg != IntPtr.Zero && _vdm.GetWindowDesktopId(fg, out var id) == 0 && id != Guid.Empty)
                {
                    current = id;
                }

                var switched = current != Guid.Empty && _lastDesktop != Guid.Empty && current != _lastDesktop;
                if (current != Guid.Empty) _lastDesktop = current;

                var moved = false;
                foreach (var hwnd in _windows())
                {
                    if (hwnd == IntPtr.Zero) continue;
                    if (_vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) != 0 || on != 0) continue;

                    if (current != Guid.Empty && _vdm.MoveWindowToDesktop(hwnd, ref current) == 0)
                    {
                        moved = true;
                        continue;
                    }

                    // Empty desktop (no foreground window to learn the id from): re-showing a window
                    // places it on the active desktop.
                    Native.ShowWindow(hwnd, Native.SW_HIDE);
                    Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                    moved = true;
                }

                if (switched || moved)
                {
                    SwitchCount++;
                    _log.Info($"Virtual desktop switch detected (moved overlay: {moved}).");
                    _onDesktopSwitched();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Virtual desktop check failed", ex);
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }

            if (_vdm != null) Marshal.ReleaseComObject(_vdm);
        }
    }
}
