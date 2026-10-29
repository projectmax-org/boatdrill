using System.Buffers.Binary;
using System.IO.Compression;

namespace Boatdrill.IconGen;

/// <summary>
/// Draws the Boatdrill mark and packs it into a multi-resolution .ico, plus the PNG the title bar uses.
/// <para>
/// The mark follows the Project Max family: a rounded tile in a two-stop gradient with one white shape
/// on it. Boatdrill's shape is a life ring, white with four orange bands, which still reads at 16 pixels
/// in the notification area. It is drawn here with plain arithmetic and written as PNG by hand, so the
/// tool runs on any OS with nothing but the .NET SDK.
/// </para>
/// Run with: dotnet run --project tools/IconGen -- [assetsDirectory] [--preview previewDirectory]
/// </summary>
internal static class Program
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    // Brush.AccentGradient in the palettes uses the same two stops.
    private static readonly Rgb TileStart = new(0x25, 0x63, 0xEB);
    private static readonly Rgb TileEnd = new(0x08, 0x91, 0xB2);
    private static readonly Rgb RingWhite = new(0xFF, 0xFF, 0xFF);
    private static readonly Rgb RingBand = new(0xF9, 0x73, 0x16);

    private const double CornerRadius = 0.25;
    private const double RingOuter = 0.335;
    private const double RingInner = 0.175;
    private const double BandDegrees = 36;

    private static int Main(string[] args)
    {
        string? preview = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--preview" && i + 1 < args.Length) preview = args[++i];
            else positional.Add(args[i]);
        }

        var output = Path.GetFullPath(positional.Count > 0 ? positional[0] : Path.Combine(RepositoryRoot(), "src", "Boatdrill", "Assets"));
        Directory.CreateDirectory(output);

        var frames = IconSizes.ToDictionary(size => size, size => EncodePng(Render(size), size, size));
        WriteIco(Path.Combine(output, "app.ico"), frames);
        File.WriteAllBytes(Path.Combine(output, "logo-512.png"), EncodePng(Render(512), 512, 512));
        Console.WriteLine("Wrote app.ico and logo-512.png to " + output);

        if (preview is not null) WritePreview(Path.GetFullPath(preview));
        return 0;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Boatdrill.slnx"))) return dir.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>Straight-alpha RGBA, row by row. Every pixel averages a grid of samples, so edges are smooth.</summary>
    private static byte[] Render(int size)
    {
        var grid = size <= 64 ? 8 : 4;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var sy = 0; sy < grid; sy++)
                {
                    for (var sx = 0; sx < grid; sx++)
                    {
                        var u = (x + (sx + 0.5) / grid) / size;
                        var v = (y + (sy + 0.5) / grid) / size;
                        if (Sample(u, v) is not { } colour) continue;
                        r += colour.R;
                        g += colour.G;
                        b += colour.B;
                        a += 1;
                    }
                }

                var i = (y * size + x) * 4;
                if (a == 0) continue;
                pixels[i] = (byte)Math.Round(r / a);
                pixels[i + 1] = (byte)Math.Round(g / a);
                pixels[i + 2] = (byte)Math.Round(b / a);
                pixels[i + 3] = (byte)Math.Round(255 * a / (grid * grid));
            }
        }

        return pixels;
    }

    /// <summary>The colour at a point of the unit tile, or null outside it.</summary>
    private static Rgb? Sample(double u, double v)
    {
        if (!InsideRoundedSquare(u, v, CornerRadius)) return null;

        double dx = u - 0.5, dy = v - 0.5;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance >= RingInner && distance <= RingOuter)
        {
            // Bands centred on the diagonals, the way a real life ring is roped.
            var degrees = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 90;
            return Math.Abs(degrees - 45) <= BandDegrees / 2 ? RingBand : RingWhite;
        }

        return Rgb.Lerp(TileStart, TileEnd, (u + v) / 2);
    }

    private static bool InsideRoundedSquare(double u, double v, double radius)
    {
        var cx = Math.Clamp(u, radius, 1 - radius);
        var cy = Math.Clamp(v, radius, 1 - radius);
        double dx = u - cx, dy = v - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // no filter
                zlib.Write(rgba, y * width * 4, width * 4);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    /// <summary>An ICO whose frames are PNG, which Windows has read at every size since Vista.</summary>
    private static void WriteIco(string path, Dictionary<int, byte[]> frames)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        var ordered = frames.OrderBy(f => f.Key).ToList();

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)ordered.Count);

        var offset = 6 + ordered.Count * 16;
        foreach (var (size, data) in ordered)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in ordered) writer.Write(data);
    }

    /// <summary>Each small size blown up with hard pixels, on a light and a dark strip, to judge legibility.</summary>
    private static void WritePreview(string directory)
    {
        Directory.CreateDirectory(directory);
        int[] sizes = [16, 20, 24, 32, 48];
        const int scale = 8, gap = 16;
        var width = sizes.Sum(s => s * scale) + gap * (sizes.Length + 1);
        var height = 48 * scale + gap * 2;
        var sheet = new byte[width * height * 2 * 4];

        for (var band = 0; band < 2; band++)
        {
            byte shade = band == 0 ? (byte)0xF3 : (byte)0x20;
            for (var y = band * height; y < (band + 1) * height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    sheet[i] = sheet[i + 1] = sheet[i + 2] = shade;
                    sheet[i + 3] = 255;
                }
            }

            var left = gap;
            foreach (var size in sizes)
            {
                var icon = Render(size);
                for (var y = 0; y < size * scale; y++)
                {
                    for (var x = 0; x < size * scale; x++)
                    {
                        var s = ((y / scale) * size + x / scale) * 4;
                        var d = ((band * height + gap + y) * width + left + x) * 4;
                        var alpha = icon[s + 3] / 255.0;
                        for (var c = 0; c < 3; c++) sheet[d + c] = (byte)Math.Round(icon[s + c] * alpha + sheet[d + c] * (1 - alpha));
                    }
                }

                left += size * scale + gap;
            }
        }

        File.WriteAllBytes(Path.Combine(directory, "icon-preview.png"), EncodePng(sheet, width, height * 2));
        Console.WriteLine("Wrote icon-preview.png to " + directory);
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
