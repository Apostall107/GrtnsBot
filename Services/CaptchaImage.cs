using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LfgBot.Services;

/// <summary>
/// Картинка-капча без внешних библиотек: свой шрифт 5×7, поворот символов, волновое искажение,
/// линии и шум; кодирование в PNG вручную (ZLibStream + CRC32). Работает одинаково на Windows и Linux.
/// </summary>
public static class CaptchaImage
{
    /// <summary>Без похожих символов: 0/O, 1/I/L, 2/Z, 5/S, 8/B.</summary>
    public const string Alphabet = "ACDEFGHJKMNPQRTUVWXY34679";

    public const int Width = 260;
    public const int Height = 96;

    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
        ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01111"],
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['J'] = ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
        ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
        ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
        ['N'] = ["10001", "10001", "11001", "10101", "10011", "10001", "10001"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['W'] = ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
        ['X'] = ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['3'] = ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    };

    public static string NewCode(int length = 5)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    public static byte[] RenderPng(string code)
    {
        var rnd = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
        var layer = new byte[Width * Height * 3];

        // Фон: светлые волны + зерно
        var phase = rnd.NextDouble() * Math.PI * 2;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var wave = 14 * Math.Sin(x / 21.0 + phase) + 10 * Math.Cos(y / 13.0 - phase);
            Set(layer, x, y,
                Clamp(222 + wave + rnd.Next(-14, 15)),
                Clamp(226 - wave + rnd.Next(-14, 15)),
                Clamp(230 + wave / 2 + rnd.Next(-14, 15)));
        }

        // Линии позади текста
        for (var i = 0; i < 4; i++) Line(layer, rnd, RandomColor(rnd, 120, 200), 1);

        // Символы: свой масштаб, поворот и цвет у каждого
        var spacing = (Width - 30.0) / code.Length;
        for (var i = 0; i < code.Length; i++)
        {
            if (!Font.TryGetValue(code[i], out var glyph)) continue;
            var scale = 7.0 + rnd.NextDouble() * 2.2;
            var angle = (rnd.NextDouble() - 0.5) * 0.5;
            var cx = 15 + spacing * (i + 0.5) + rnd.Next(-4, 5);
            var cy = Height / 2.0 + rnd.Next(-8, 9);
            DrawGlyph(layer, glyph, cx, cy, scale, angle, RandomColor(rnd, 20, 110));
        }

        // Волновое искажение всей картинки
        var image = new byte[layer.Length];
        double ax = 2 + rnd.NextDouble() * 1.5, ay = 2.5 + rnd.NextDouble() * 2;
        double px = 70 + rnd.NextDouble() * 40, py = 45 + rnd.NextDouble() * 25;
        double p1 = rnd.NextDouble() * Math.PI * 2, p2 = rnd.NextDouble() * Math.PI * 2;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var sx = Math.Clamp(x + (int)(ax * Math.Sin(2 * Math.PI * y / py + p1)), 0, Width - 1);
            var sy = Math.Clamp(y + (int)(ay * Math.Sin(2 * Math.PI * x / px + p2)), 0, Height - 1);
            Buffer.BlockCopy(layer, (sy * Width + sx) * 3, image, (y * Width + x) * 3, 3);
        }

        // Шум поверх: линии через текст и точки
        for (var i = 0; i < 3; i++) Line(image, rnd, RandomColor(rnd, 40, 140), 2);
        for (var i = 0; i < 700; i++)
        {
            var c = RandomColor(rnd, 30, 220);
            Set(image, rnd.Next(Width), rnd.Next(Height), c.R, c.G, c.B);
        }

        return EncodePng(image, Width, Height);
    }

    private static void DrawGlyph(byte[] img, string[] glyph, double cx, double cy, double scale, double angle,
        (byte R, byte G, byte B) color)
    {
        double gw = 5 * scale, gh = 7 * scale;
        var radius = (int)Math.Ceiling(Math.Max(gw, gh) * 0.75);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);

        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            // Обратный поворот: координата пикселя → координата в глифе
            var gx = cos * dx + sin * dy + gw / 2;
            var gy = -sin * dx + cos * dy + gh / 2;
            var col = (int)Math.Floor(gx / scale);
            var row = (int)Math.Floor(gy / scale);
            if (row is < 0 or > 6 || col is < 0 or > 4 || glyph[row][col] != '1') continue;

            var x = (int)Math.Round(cx + dx);
            var y = (int)Math.Round(cy + dy);
            if (x is >= 0 and < Width && y is >= 0 and < Height)
                Set(img, x, y, color.R, color.G, color.B);
        }
    }

    /// <summary>Волнистая линия через всю ширину.</summary>
    private static void Line(byte[] img, Random rnd, (byte R, byte G, byte B) color, int thickness)
    {
        double y0 = rnd.Next(Height), y1 = rnd.Next(Height);
        double amp = rnd.Next(3, 12), period = rnd.Next(25, 70), phase = rnd.NextDouble() * Math.PI * 2;
        for (var x = 0; x < Width; x++)
        {
            var y = (int)(y0 + (y1 - y0) * x / Width + amp * Math.Sin(x / period * Math.PI * 2 + phase));
            for (var t = 0; t < thickness; t++)
                if (y + t is >= 0 and < Height) Set(img, x, y + t, color.R, color.G, color.B);
        }
    }

    private static (byte R, byte G, byte B) RandomColor(Random rnd, int min, int max) =>
        ((byte)rnd.Next(min, max), (byte)rnd.Next(min, max), (byte)rnd.Next(min, max));

    private static void Set(byte[] img, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * Width + x) * 3;
        img[i] = r;
        img[i + 1] = g;
        img[i + 2] = b;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    // ---------- PNG ----------

    private static byte[] EncodePng(byte[] rgb, int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // бит на канал
        header[9] = 2; // RGB
        WriteChunk(png, "IHDR", header);

        var stride = width * 3;
        var raw = new byte[height * (stride + 1)]; // перед каждой строкой байт фильтра 0
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgb, y * stride, raw, y * (stride + 1) + 1, stride);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
