using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using Brainbox.Desktop.Interop;

namespace Brainbox.Desktop.Shell
{
    /// <summary>
    /// System-wide hotkeys (§13) registered on a private message-only window, so they never collide
    /// with the classic Translumo hotkey plumbing. Gestures are strings like "Ctrl+Alt+B".
    /// </summary>
    public sealed class GlobalHotkeys : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private int _nextId = 0xB000;

        public GlobalHotkeys()
        {
            var p = new HwndSourceParameters("BrainboxHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }; // HWND_MESSAGE
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <summary>Registers a gesture; returns null on success or a human readable error.</summary>
        public string Register(string gesture, Action action)
        {
            if (string.IsNullOrWhiteSpace(gesture)) return null;
            if (!TryParse(gesture, out var mods, out var vk)) return $"'{gesture}' is not a valid shortcut.";
            var id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
                return $"{gesture} is already used by another application.";
            _actions[id] = action;
            return null;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        public static bool TryParse(string gesture, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            Key? key = null;
            foreach (var raw in gesture.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= Native.MOD_CONTROL;
                        break;
                    case "alt":
                        modifiers |= Native.MOD_ALT;
                        break;
                    case "shift":
                        modifiers |= Native.MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                        modifiers |= Native.MOD_WIN;
                        break;
                    default:
                        if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                        if (Enum.TryParse<Key>(part, true, out var k)) key = k;
                        else return false;
                        break;
                }
            }

            if (key == null || modifiers == 0) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
            return vk != 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
