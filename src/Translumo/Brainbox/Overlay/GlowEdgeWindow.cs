using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Brainbox.Core.Capture;
using Brainbox.Core.Geometry;

namespace Brainbox.Desktop.Overlay
{
    public enum GlowEdge { Top, Bottom, Left, Right }

    /// <summary>
    /// The "Brainbox is live" indicator (§11): a soft violet→cyan light along one screen edge.
    /// Each monitor gets four thin strip windows instead of one full-screen layer, so the slow
    /// breathing animation only re-composes a few hundred KB of pixels (GPU/CPU friendly), and the
    /// strips are click-through and excluded from capture like the translation overlay.
    /// </summary>
    public sealed class GlowEdgeWindow : ClickThroughWindow
    {
        public static readonly Color BrandViolet = Color.FromRgb(0x8B, 0x5C, 0xF6);
        public static readonly Color BrandCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
        private const double ThicknessDip = 22;

        private readonly Grid _breath = new() { IsHitTestVisible = false };
        private readonly Grid _level = new() { IsHitTestVisible = false };
        private double _intensity = 0.5;
        private bool _active;

        public GlowEdgeWindow(MonitorInfo monitor, GlowEdge edge) : base(OverlaySurface.Layered)
        {
            Monitor = monitor;
            Edge = edge;
            Title = $"Brainbox glow {edge} {monitor.DeviceName}";

            var horizontal = edge is GlowEdge.Top or GlowEdge.Bottom;
            var along = new LinearGradientBrush
            {
                StartPoint = horizontal ? new Point(0, 0) : new Point(0, 0),
                EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, BrandViolet.R, BrandViolet.G, BrandViolet.B), 0.0),
                    new GradientStop(BrandViolet, 0.18),
                    new GradientStop(BrandCyan, 0.5),
                    new GradientStop(BrandViolet, 0.82),
                    new GradientStop(Color.FromArgb(0, BrandViolet.R, BrandViolet.G, BrandViolet.B), 1.0),
                },
            };
            var (s, e) = edge switch
            {
                GlowEdge.Top => (new Point(0, 0), new Point(0, 1)),
                GlowEdge.Bottom => (new Point(0, 1), new Point(0, 0)),
                GlowEdge.Left => (new Point(0, 0), new Point(1, 0)),
                _ => (new Point(1, 0), new Point(0, 0)),
            };
            var fade = new LinearGradientBrush
            {
                StartPoint = s,
                EndPoint = e,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(110, 0, 0, 0), 0.25),
                    new GradientStop(Color.FromArgb(25, 0, 0, 0), 0.6),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0),
                },
            };
            var rect = new Rectangle { Fill = along, OpacityMask = fade, IsHitTestVisible = false };
            _level.Children.Add(rect);

            // Small four-point sparkles in the corners (top/bottom strips only).
            if (horizontal)
            {
                _level.Children.Add(Sparkle(HorizontalAlignment.Left, edge));
                _level.Children.Add(Sparkle(HorizontalAlignment.Right, edge));
            }

            _breath.Children.Add(_level);
            Content = _breath;
            Loaded += (_, _) => StartBreathing();
        }

        public MonitorInfo Monitor { get; private set; }
        public GlowEdge Edge { get; }

        public void UpdateMonitor(MonitorInfo monitor)
        {
            Monitor = monitor;
            PlaceAt(BoundsFor(monitor, Edge));
        }

        public static PixelRect BoundsFor(MonitorInfo m, GlowEdge edge)
        {
            var t = (int)Math.Ceiling(ThicknessDip * (m.DpiScale <= 0 ? 1 : m.DpiScale));
            var b = m.Bounds;
            return edge switch
            {
                GlowEdge.Top => new PixelRect(b.X, b.Y, b.Width, t),
                GlowEdge.Bottom => new PixelRect(b.X, b.Bottom - t, b.Width, t),
                GlowEdge.Left => new PixelRect(b.X, b.Y + t, t, Math.Max(1, b.Height - 2 * t)),
                _ => new PixelRect(b.Right - t, b.Y + t, t, Math.Max(1, b.Height - 2 * t)),
            };
        }

        /// <summary>0..1 user intensity. Idle glow stays very low-key; activity adds a gentle pulse.</summary>
        public void SetIntensity(double intensity)
        {
            _intensity = Math.Clamp(intensity, 0.05, 1);
            ApplyLevel(animate: true);
        }

        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            ApplyLevel(animate: true);
        }

        private void ApplyLevel(bool animate)
        {
            var idle = 0.18 + 0.55 * _intensity;           // subtle by default
            var target = _active ? Math.Min(1, idle * 1.55) : idle;
            if (!animate)
            {
                _level.Opacity = target;
                return;
            }

            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(_active ? 220 : 900))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(anim, 30);
            _level.BeginAnimation(OpacityProperty, anim);
        }

        private void StartBreathing()
        {
            ApplyLevel(animate: false);
            var breathe = new DoubleAnimation(0.62, 1.0, TimeSpan.FromSeconds(3.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // Low frame rate: a slow breath does not need 60 fps (keeps CPU/GPU near zero).
            Timeline.SetDesiredFrameRate(breathe, 15);
            _breath.BeginAnimation(OpacityProperty, breathe);
        }

        private static UIElement Sparkle(HorizontalAlignment side, GlowEdge edge)
        {
            var star = new TextBlock
            {
                Text = "✦",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 0xE9, 0xE3, 0xFF)),
                HorizontalAlignment = side,
                VerticalAlignment = edge == GlowEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = new Thickness(side == HorizontalAlignment.Left ? 10 : 0, 3, side == HorizontalAlignment.Right ? 10 : 0, 3),
                IsHitTestVisible = false,
            };
            var twinkle = new DoubleAnimation(0.25, 0.9, TimeSpan.FromSeconds(side == HorizontalAlignment.Left ? 2.3 : 2.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(twinkle, 10);
            star.BeginAnimation(OpacityProperty, twinkle);
            return star;
        }
    }
}
