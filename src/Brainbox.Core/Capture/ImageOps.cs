using System.Buffers.Binary;
using System.IO.Compression;
using Brainbox.Core.Geometry;

namespace Brainbox.Core.Capture;

/// <summary>Small pixel utilities on BGRA buffers (no System.Drawing dependency).</summary>
public static class ImageOps
{
    /// <summary>
    /// Estimates background and text colours of a text box: the background is the average of the
    /// box border pixels (text rarely touches the box edge), the foreground is the average of the
    /// interior pixels that differ most from that background.
    /// </summary>
    public static (int BackgroundRgb, int ForegroundRgb) SampleColors(DesktopFrame frame, PixelRect box)
    {
        var r = box.Inflate(2, 2).Intersect(frame.Bounds);
        if (r.IsEmpty) return (0x202020, 0xFFFFFF);

        long br = 0, bg = 0, bb = 0, bn = 0;
        var px = frame.Bgra;
        void Add(int x, int y)
        {
            var o = (y - frame.Bounds.Y) * frame.Stride + (x - frame.Bounds.X) * 4;
            bb += px[o];
            bg += px[o + 1];
            br += px[o + 2];
            bn++;
        }

        var stepX = Math.Max(1, r.Width / 40);
        var stepY = Math.Max(1, r.Height / 10);
        for (var x = r.Left; x < r.Right; x += stepX)
        {
            Add(x, r.Top);
            Add(x, r.Bottom - 1);
        }

        for (var y = r.Top; y < r.Bottom; y += stepY)
        {
            Add(r.Left, y);
            Add(r.Right - 1, y);
        }

        var bgR = (int)(br / Math.Max(1, bn));
        var bgG = (int)(bg / Math.Max(1, bn));
        var bgB = (int)(bb / Math.Max(1, bn));

        // Foreground: interior pixels far from the background colour.
        long fr = 0, fg = 0, fb = 0, fn = 0;
        var inner = box.Intersect(frame.Bounds);
        var sx = Math.Max(1, inner.Width / 60);
        var sy = Math.Max(1, inner.Height / 12);
        for (var y = inner.Top; y < inner.Bottom; y += sy)
        {
            for (var x = inner.Left; x < inner.Right; x += sx)
            {
                var o = (y - frame.Bounds.Y) * frame.Stride + (x - frame.Bounds.X) * 4;
                int b = px[o], g = px[o + 1], rr = px[o + 2];
                var d = Math.Abs(rr - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB);
                if (d < 120) continue;
                fr += rr;
                fg += g;
                fb += b;
                fn++;
            }
        }

        var bgRgb = (bgR << 16) | (bgG << 8) | bgB;
        var fgRgb = fn == 0
            ? (Luminance(bgRgb) > 0.5 ? 0x101010 : 0xF5F5F5)
            : ((int)(fr / fn) << 16) | ((int)(fg / fn) << 8) | (int)(fb / fn);
        return (bgRgb, fgRgb);
    }

    /// <summary>Relative luminance 0..1 of a 0xRRGGBB colour.</summary>
    public static double Luminance(int rgb)
    {
        static double Lin(int c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin((rgb >> 16) & 0xFF) + 0.7152 * Lin((rgb >> 8) & 0xFF) + 0.0722 * Lin(rgb & 0xFF);
    }

    /// <summary>WCAG contrast ratio between two colours (1..21).</summary>
    public static double Contrast(int rgbA, int rgbB)
    {
        var a = Luminance(rgbA);
        var b = Luminance(rgbB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Bilinear resize of a tightly packed BGRA image.</summary>
    public static byte[] Resize(byte[] src, int w, int h, int newW, int newH)
    {
        var dst = new byte[newW * newH * 4];
        var xRatio = (double)(w - 1) / Math.Max(1, newW - 1);
        var yRatio = (double)(h - 1) / Math.Max(1, newH - 1);
        for (var y = 0; y < newH; y++)
        {
            var fy = y * yRatio;
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, h - 1);
            var wy = fy - y0;
            for (var x = 0; x < newW; x++)
            {
                var fx = x * xRatio;
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, w - 1);
                var wx = fx - x0;
                var o = (y * newW + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var a = src[(y0 * w + x0) * 4 + c];
                    var b = src[(y0 * w + x1) * 4 + c];
                    var cc = src[(y1 * w + x0) * 4 + c];
                    var d = src[(y1 * w + x1) * 4 + c];
                    var top = a + (b - a) * wx;
                    var bottom = cc + (d - cc) * wx;
                    dst[o + c] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * wy), 0, 255);
                }
            }
        }

        return dst;
    }

    /// <summary>Encodes a tightly packed BGRA image as PNG (for vision models and debug snapshots).</summary>
    public static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(ms, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                for (var x = 0; x < width; x++)
                {
                    var s = (y * width + x) * 4;
                    var d = 1 + x * 4;
                    row[d] = bgra[s + 2];
                    row[d + 1] = bgra[s + 1];
                    row[d + 2] = bgra[s];
                    row[d + 3] = 255;
                }

                z.Write(row);
            }
        }

        WriteChunk(ms, "IDAT", raw.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }

        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
