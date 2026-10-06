using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brainbox.Core.Capture;
using Brainbox.Core.Overlay;
using Brainbox.Core.Settings;

namespace Brainbox.Desktop.Overlay
{
    /// <summary>
    /// One full-monitor, click-through, capture-excluded layer that draws the translations for that
    /// monitor at the positions decided by <see cref="OverlayLayout"/>. Items are diffed by id so
    /// unchanged translations are not re-created (no flicker), new ones fade in, gone ones vanish.
    /// </summary>
    public sealed class TranslationOverlayWindow : ClickThroughWindow
    {
        private readonly Canvas _canvas = new() { IsHitTestVisible = false };
        private readonly Dictionary<long, (Border Border, OverlayItem Item)> _items = new();

        public TranslationOverlayWindow(MonitorInfo monitor)
        {
            Monitor = monitor;
            Content = _canvas;
            Title = "Brainbox translation overlay " + monitor.DeviceName;
        }

        public MonitorInfo Monitor { get; private set; }

        public int ItemCount => _items.Count;

        public IEnumerable<OverlayItem> Items => _items.Values.Select(v => v.Item);

        public void UpdateMonitor(MonitorInfo monitor)
        {
            Monitor = monitor;
            PlaceAt(monitor.Bounds);
        }

        public void Render(IReadOnlyList<OverlayItem> items, BrainboxSettings settings)
        {
            var scale = DpiScale;
            if (scale <= 0) scale = Monitor.DpiScale;
            var keep = new HashSet<long>();

            foreach (var item in items)
            {
                keep.Add(item.Id);
                if (_items.TryGetValue(item.Id, out var existing))
                {
                    if (existing.Item == item) continue;
                    Apply(existing.Border, item, settings, scale);
                    _items[item.Id] = (existing.Border, item);
                    continue;
                }

                var border = CreateBorder();
                Apply(border, item, settings, scale);
                _canvas.Children.Add(border);
                _items[item.Id] = (border, item);
                border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            }

            foreach (var id in _items.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _canvas.Children.Remove(_items[id].Border);
                _items.Remove(id);
            }

            if (_items.Count > 0) BringToTop();
        }

        public void ClearItems()
        {
            _canvas.Children.Clear();
            _items.Clear();
        }

        private static Border CreateBorder()
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                IsHitTestVisible = false,
            };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.ClearType);
            return new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
        }

        private void Apply(Border border, OverlayItem item, BrainboxSettings settings, double scale)
        {
            var text = (TextBlock)border.Child;
            text.Text = item.Text;
            text.FontFamily = new FontFamily(settings.FontFamily);
            text.FontSize = Math.Max(6, item.FontSizePx / scale);
            text.Foreground = new SolidColorBrush(FromRgb(item.ForegroundRgb, 255));
            text.LineHeight = text.FontSize * 1.22;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

            var bgAlpha = (byte)Math.Clamp((int)Math.Round(item.BackgroundAlpha * 255), 0, 255);
            border.Background = new SolidColorBrush(FromRgb(item.BackgroundRgb, bgAlpha));
            // Hairline accent so translations are recognisable as Brainbox output without shouting.
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(item.Style == OverlayStyle.Replace ? (byte)70 : (byte)140, 0x8B, 0x5C, 0xF6));
            border.BorderThickness = new Thickness(item.Style == OverlayStyle.Replace ? 0.75 : 1);
            border.CornerRadius = new CornerRadius(item.Style == OverlayStyle.Bubble ? 8 : 3);
            border.Padding = new Thickness(text.FontSize * 0.3, text.FontSize * 0.05, text.FontSize * 0.3, text.FontSize * 0.05);

            var x = (item.Box.X - Monitor.Bounds.X) / scale;
            var y = (item.Box.Y - Monitor.Bounds.Y) / scale;
            border.Width = item.Box.Width / scale;
            border.MinHeight = item.Box.Height / scale;
            border.MaxHeight = (Monitor.Bounds.Bottom - item.Box.Y) / scale;
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
        }

        private static Color FromRgb(int rgb, byte alpha) =>
            Color.FromArgb(alpha, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }
}
