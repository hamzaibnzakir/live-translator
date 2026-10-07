using System;
using System.Collections.Generic;
using System.Linq;
using Brainbox.Core.Capture;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Geometry;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace Brainbox.Desktop.Capture
{
    /// <summary>
    /// GPU capture via DXGI Desktop Duplication, one duplication per monitor across all adapters
    /// (hybrid-GPU laptops included: each output is duplicated on the adapter that drives it).
    /// Frames are only copied when the GPU reports a new frame, so a static desktop costs almost
    /// nothing. Rotated (portrait) outputs and any output that cannot be duplicated are captured
    /// with GDI instead, so a single quirky monitor never breaks the whole capture.
    /// Respects WDA_EXCLUDEFROMCAPTURE like GDI does.
    /// </summary>
    public sealed class DxgiFrameSource : IFrameSource
    {
        private sealed class Dup : IDisposable
        {
            public string Device;
            public PixelRect Bounds;
            public Device D3D;
            public OutputDuplication Duplication;
            public Texture2D Staging;
            public byte[] Pixels; // last frame, tightly packed BGRA, desktop orientation
            public bool HasFrame;
            public bool UseGdi;

            public void Dispose()
            {
                Duplication?.Dispose();
                Staging?.Dispose();
                Duplication = null;
                Staging = null;
            }
        }

        private readonly object _gate = new();
        private readonly ILog _log;
        private readonly GdiFrameSource _gdi = new();
        private readonly List<Dup> _dups = new();
        private readonly List<Device> _devices = new();
        private string _layout = "";

        public DxgiFrameSource(ILog log)
        {
            _log = log;
            lock (_gate) Initialize();
            if (_dups.Count == 0 || _dups.All(d => d.UseGdi)) throw new NotSupportedException("Desktop Duplication is not available on this system.");
        }

        public bool HonoursCaptureExclusion => _gdi.HonoursCaptureExclusion;

        public IReadOnlyList<MonitorInfo> GetMonitors() => _gdi.GetMonitors();

        public DesktopFrame Capture(PixelRect area)
        {
            if (!GdiFrameSource.IsInputDesktopAccessible()) return null;
            lock (_gate)
            {
                var monitors = _gdi.GetMonitors();
                var layout = MonitorInfo.LayoutSignature(monitors);
                if (layout != _layout) Initialize();

                var stride = area.Width * 4;
                var buffer = new byte[stride * area.Height];
                foreach (var dup in _dups)
                {
                    var part = dup.Bounds.Intersect(area);
                    if (part.IsEmpty) continue;

                    if (dup.UseGdi || !Refresh(dup))
                    {
                        if (!dup.UseGdi && !dup.HasFrame) return null; // duplication lost; next tick re-initialises
                        if (dup.UseGdi)
                        {
                            var g = _gdi.Capture(part);
                            if (g == null) return null;
                            Blit(g.Bgra, g.Stride, part, part, buffer, stride, area);
                            continue;
                        }
                    }

                    Blit(dup.Pixels, dup.Bounds.Width * 4, dup.Bounds, part, buffer, stride, area);
                }

                return new DesktopFrame(area, buffer, stride, Environment.TickCount64, monitors);
            }
        }

        private static void Blit(byte[] src, int srcStride, PixelRect srcRect, PixelRect part, byte[] dst, int dstStride, PixelRect dstRect)
        {
            var rowBytes = part.Width * 4;
            for (var y = part.Top; y < part.Bottom; y++)
            {
                var s = (y - srcRect.Y) * srcStride + (part.X - srcRect.X) * 4;
                var d = (y - dstRect.Y) * dstStride + (part.X - dstRect.X) * 4;
                System.Buffer.BlockCopy(src, s, dst, d, rowBytes);
            }
        }

        /// <summary>Pulls a new frame if the GPU has one. Returns false when the duplication was lost.</summary>
        private bool Refresh(Dup dup)
        {
            if (dup.Duplication == null)
            {
                Initialize();
                return false;
            }

            SharpDX.DXGI.Resource resource = null;
            try
            {
                var result = dup.Duplication.TryAcquireNextFrame(dup.HasFrame ? 0 : 200, out var info, out resource);
                if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code) return dup.HasFrame;
                if (result.Failure)
                {
                    _log.Info($"Desktop duplication lost on {dup.Device} ({result.Code:X}); re-initialising.");
                    dup.Dispose();
                    return false;
                }

                if (info.LastPresentTime == 0 && dup.HasFrame)
                {
                    dup.Duplication.ReleaseFrame();
                    return true; // only the pointer moved
                }

                using (var tex = resource.QueryInterface<Texture2D>())
                {
                    dup.D3D.ImmediateContext.CopyResource(tex, dup.Staging);
                }

                var box = dup.D3D.ImmediateContext.MapSubresource(dup.Staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var w = dup.Bounds.Width;
                    var h = dup.Bounds.Height;
                    dup.Pixels ??= new byte[w * h * 4];
                    for (var y = 0; y < h; y++)
                    {
                        Utilities.Read(box.DataPointer + y * box.RowPitch, dup.Pixels, y * w * 4, w * 4);
                    }
                }
                finally
                {
                    dup.D3D.ImmediateContext.UnmapSubresource(dup.Staging, 0);
                }

                dup.Duplication.ReleaseFrame();
                dup.HasFrame = true;
                return true;
            }
            catch (SharpDXException ex)
            {
                _log.Info($"Desktop duplication error on {dup.Device}: {ex.ResultCode}; re-initialising.");
                dup.Dispose();
                return false;
            }
            finally
            {
                resource?.Dispose();
            }
        }

        private void Initialize()
        {
            foreach (var d in _dups) d.Dispose();
            _dups.Clear();
            foreach (var dev in _devices) dev.Dispose();
            _devices.Clear();

            var monitors = _gdi.GetMonitors();
            _layout = MonitorInfo.LayoutSignature(monitors);

            using var factory = new Factory1();
            for (var a = 0; a < factory.GetAdapterCount1(); a++)
            {
                using var adapter = factory.GetAdapter1(a);
                Device device = null;
                for (var o = 0; o < adapter.GetOutputCount(); o++)
                {
                    using var output = adapter.GetOutput(o);
                    var desc = output.Description;
                    if (!desc.IsAttachedToDesktop) continue;

                    var bounds = PixelRect.FromLTRB(desc.DesktopBounds.Left, desc.DesktopBounds.Top, desc.DesktopBounds.Right, desc.DesktopBounds.Bottom);
                    var dup = new Dup { Device = desc.DeviceName, Bounds = bounds };
                    _dups.Add(dup);

                    if (desc.Rotation is not (DisplayModeRotation.Identity or DisplayModeRotation.Unspecified))
                    {
                        dup.UseGdi = true; // rotated panels: GDI already returns desktop orientation
                        continue;
                    }

                    try
                    {
                        if (device == null)
                        {
                            device = new Device(adapter);
                            _devices.Add(device);
                        }

                        using var output1 = output.QueryInterface<Output1>();
                        dup.D3D = device;
                        dup.Duplication = output1.DuplicateOutput(device);
                        dup.Staging = new Texture2D(device, new Texture2DDescription
                        {
                            CpuAccessFlags = CpuAccessFlags.Read,
                            BindFlags = BindFlags.None,
                            Format = Format.B8G8R8A8_UNorm,
                            Width = bounds.Width,
                            Height = bounds.Height,
                            OptionFlags = ResourceOptionFlags.None,
                            MipLevels = 1,
                            ArraySize = 1,
                            SampleDescription = { Count = 1, Quality = 0 },
                            Usage = ResourceUsage.Staging,
                        });
                    }
                    catch (SharpDXException ex)
                    {
                        _log.Info($"Desktop duplication unavailable for {desc.DeviceName} ({ex.ResultCode}); using GDI for that monitor.");
                        dup.Dispose();
                        dup.UseGdi = true;
                    }
                }
            }

            // Monitors DXGI did not report (rare: indirect displays) are captured with GDI.
            foreach (var m in monitors.Where(m => _dups.All(d => d.Bounds != m.Bounds)))
            {
                _dups.Add(new Dup { Device = m.DeviceName, Bounds = m.Bounds, UseGdi = true });
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var d in _dups) d.Dispose();
                _dups.Clear();
                foreach (var dev in _devices) dev.Dispose();
                _devices.Clear();
                _gdi.Dispose();
            }
        }
    }
}
