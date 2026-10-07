using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using Brainbox.Core.Capture;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using Brainbox.Desktop.Capture;
using Brainbox.Desktop.Overlay;

namespace Brainbox.Desktop.SelfTest
{
    /// <summary>
    /// Measures, on the real machine, which overlay surface technique supports all three of:
    /// capture exclusion, visible drawing, and a transparent background. Also checks whether
    /// Desktop Duplication returns the same image as GDI.
    /// </summary>
    public static class SurfaceProbe
    {
        public sealed record SurfaceResult(OverlaySurface Surface, bool AffinityAccepted, int Win32Error, bool HiddenFromCapture, bool DrawsContent, bool TransparentBackground)
        {
            public bool FullyWorks => AffinityAccepted && HiddenFromCapture && DrawsContent && TransparentBackground;

            public override string ToString() =>
                $"{Surface}: affinity accepted={AffinityAccepted} (err {Win32Error}), hidden from capture={HiddenFromCapture}, draws={DrawsContent}, transparent background={TransparentBackground}";
        }

        public static async Task<List<SurfaceResult>> ProbeSurfacesAsync(MonitorInfo monitor)
        {
            var results = new List<SurfaceResult>();
            var rect = new PixelRect(monitor.WorkArea.X + 40, monitor.WorkArea.Bottom - 160, 240, 60);
            var gdi = new GdiFrameSource();
            foreach (var surface in new[] { OverlaySurface.Layered, OverlaySurface.Redirected })
            {
                var before = gdi.Capture(rect);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)) });
                var w = new ClickThroughWindow(surface) { Content = grid };
                w.Show();
                w.PlaceAt(rect);
                if (surface == OverlaySurface.Redirected) w.SetRegion(new[] { new PixelRect(0, 0, rect.Width, rect.Height) });
                await Task.Delay(800);
                var accepted = w.ExcludedFromCapture;
                var err = w.LastAffinityError;
                var whileExcluded = gdi.Capture(rect);
                w.SetCaptureExclusion(false);
                await Task.Delay(500);
                var visible = gdi.Capture(rect);
                w.Close();
                await Task.Delay(200);

                var hidden = MagentaFraction(whileExcluded, left: true) < 0.05;
                var draws = MagentaFraction(visible, left: true) > 0.8;
                var transparent = before != null && visible != null && SameFraction(before, visible, left: false) > 0.9;
                results.Add(new SurfaceResult(surface, accepted, err, accepted && hidden, draws, transparent));
            }

            return results;
        }

        /// <summary>Fraction of pixels that match between GDI and Desktop Duplication captures of the monitor.</summary>
        public static async Task<string> ProbeDuplicationAsync(MonitorInfo monitor, ILog log)
        {
            try
            {
                using var dx = new DxgiFrameSource(log);
                dx.Capture(monitor.Bounds);
                await Task.Delay(300);
                var a = dx.Capture(monitor.Bounds);
                var b = new GdiFrameSource().Capture(monitor.Bounds);
                if (a == null || b == null) return "Desktop Duplication returned no frame";
                long same = 0, black = 0, n = 0;
                for (var i = 0; i < a.Bgra.Length; i += 4 * 97)
                {
                    n++;
                    if (a.Bgra[i] == 0 && a.Bgra[i + 1] == 0 && a.Bgra[i + 2] == 0) black++;
                    if (Math.Abs(a.Bgra[i] - b.Bgra[i]) < 8 && Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) < 8 && Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]) < 8) same++;
                }

                return $"Desktop Duplication vs GDI: {100.0 * same / n:0.#}% identical pixels, {100.0 * black / n:0.#}% black";
            }
            catch (Exception ex)
            {
                return "Desktop Duplication unavailable: " + ex.Message;
            }
        }

        private static double MagentaFraction(DesktopFrame f, bool left)
        {
            if (f == null) return 0;
            int hit = 0, n = 0;
            var x0 = left ? 0 : f.Width / 2;
            var x1 = left ? f.Width / 2 : f.Width;
            for (var y = 5; y < f.Height - 5; y += 3)
            {
                for (var x = x0 + 5; x < x1 - 5; x += 3)
                {
                    var o = y * f.Stride + x * 4;
                    n++;
                    if (f.Bgra[o + 2] > 220 && f.Bgra[o] > 220 && f.Bgra[o + 1] < 40) hit++;
                }
            }

            return n == 0 ? 0 : (double)hit / n;
        }

        private static double SameFraction(DesktopFrame a, DesktopFrame b, bool left)
        {
            int same = 0, n = 0;
            var x0 = left ? 0 : a.Width / 2;
            var x1 = left ? a.Width / 2 : a.Width;
            for (var y = 5; y < a.Height - 5; y += 3)
            {
                for (var x = x0 + 5; x < x1 - 5; x += 3)
                {
                    var o = y * a.Stride + x * 4;
                    n++;
                    if (Math.Abs(a.Bgra[o] - b.Bgra[o]) < 10 && Math.Abs(a.Bgra[o + 1] - b.Bgra[o + 1]) < 10 && Math.Abs(a.Bgra[o + 2] - b.Bgra[o + 2]) < 10) same++;
                }
            }

            return n == 0 ? 0 : (double)same / n;
        }
    }
}
