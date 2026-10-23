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

    public const int MinLength = 4;
    public const int MaxLength = 100;
    public const int PerRow = 10;      // символов в строке
    private const int CharStep = 44;   // шаг символа, px
    private const int GroupGap = 14;   // доп. отступ после каждых 5 символов
    private const int RowStep = 80;    // высота строки, px

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

    /// <summary>Для зеркальных символов: отражённые E и 3 выглядят друг как друг.</summary>
    public const string MirrorAlphabet = "ACDFGHJKMNPQRTUVWXY4679";

    public static string NewCode(int length = 5, bool mirror = false)
    {
        length = Math.Clamp(length, MinLength, MaxLength);
        var alphabet = mirror ? MirrorAlphabet : Alphabet;
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>Убирает пробелы и регистр — так сравнивается ввод с кодом.</summary>
    public static string Normalize(string? input) =>
        new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    /// <param name="mirror">Отразить каждый символ по горизонтали (порядок символов обычный, слева направо).</param>
    /// <param name="distortion">Сила искажения, 0–100: 0 — ровный текст без шума, 100 — максимум.</param>
    public static byte[] RenderPng(string code, bool mirror = false, int distortion = 50)
    {
        var d = Math.Clamp(distortion, 0, 100) / 100.0;
        var rnd = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));

        var perRow = Math.Min(code.Length, PerRow);
        var rows = (code.Length + PerRow - 1) / PerRow;
        var gaps = (perRow - 1) / 5;
        var width = 30 + perRow * CharStep + gaps * GroupGap;
        var height = 16 + rows * RowStep;
        var canvas = new Canvas(width, height);

        // Фон: светлые волны + зерно
        var phase = rnd.NextDouble() * Math.PI * 2;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var wave = 14 * Math.Sin(x / 21.0 + phase) + 10 * Math.Cos(y / 13.0 - phase);
            canvas.Set(x, y,
                Clamp(222 + wave + rnd.Next(-14, 15)),
                Clamp(226 - wave + rnd.Next(-14, 15)),
                Clamp(230 + wave / 2 + rnd.Next(-14, 15)));
        }

        // Линии позади текста
        for (var i = 0; i < (int)Math.Round(rows * 4 * d); i++) Line(canvas, rnd, RandomColor(rnd, 120, 200), 1);

        // Символы: строки по 10, отступ после каждых 5; у каждого свой масштаб, поворот и цвет
        var maxAngle = 1.1 * d;
        var jitter = 1 + (int)Math.Round(6 * d);
        for (var i = 0; i < code.Length; i++)
        {
            if (!Font.TryGetValue(code[i], out var glyph)) continue;
            int row = i / PerRow, col = i % PerRow;
            var scale = 7.0 + rnd.NextDouble() * 2.2;
            var angle = (rnd.NextDouble() - 0.5) * maxAngle;
            var cx = 15 + CharStep * (col + 0.5) + col / 5 * GroupGap + rnd.Next(-jitter, jitter + 1) / 2;
            var cy = 8 + RowStep * (row + 0.5) + rnd.Next(-jitter, jitter + 1);
            DrawGlyph(canvas, glyph, cx, cy, scale, angle, mirror, RandomColor(rnd, 20, 110));
        }

        // Волновое искажение (сила зависит от уровня); с 50% — ещё одна волна с другим периодом
        var image = canvas;
        var passes = d <= 0 ? 0 : d >= 0.5 ? 2 : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            var k = 2.5 * d;
            double ax = (2 + rnd.NextDouble() * 1.5) * k, ay = (2.5 + rnd.NextDouble() * 2) * k;
            double px = 70 + rnd.NextDouble() * 40, py = 45 + rnd.NextDouble() * 25;
            if (pass == 1) { ax /= 2; ay /= 2; px /= 2.2; py /= 1.8; }
            image = Wave(image, ax, ay, px, py, rnd.NextDouble() * Math.PI * 2, rnd.NextDouble() * Math.PI * 2);
        }

        // Шум поверх: линии через текст и точки
        for (var i = 0; i < (int)Math.Round(rows * 3 * d); i++) Line(image, rnd, RandomColor(rnd, 40, 140), 2);
        var dots = (int)(width * height * d / 14);
        for (var i = 0; i < dots; i++)
        {
            var c = RandomColor(rnd, 30, 220);
            image.Set(rnd.Next(width), rnd.Next(height), c.R, c.G, c.B);
        }

        return EncodePng(image.Pixels, width, height);
    }

    private static Canvas Wave(Canvas src, double ax, double ay, double px, double py, double p1, double p2)
    {
        var dst = new Canvas(src.Width, src.Height);
        for (var y = 0; y < src.Height; y++)
        for (var x = 0; x < src.Width; x++)
        {
            var sx = Math.Clamp(x + (int)(ax * Math.Sin(2 * Math.PI * y / py + p1)), 0, src.Width - 1);
            var sy = Math.Clamp(y + (int)(ay * Math.Sin(2 * Math.PI * x / px + p2)), 0, src.Height - 1);
            Buffer.BlockCopy(src.Pixels, (sy * src.Width + sx) * 3, dst.Pixels, (y * src.Width + x) * 3, 3);
        }
        return dst;
    }

    private static void DrawGlyph(Canvas img, string[] glyph, double cx, double cy, double scale, double angle,
        bool mirror, (byte R, byte G, byte B) color)
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
            if (row is < 0 or > 6 || col is < 0 or > 4 || glyph[row][mirror ? 4 - col : col] != '1') continue;
            img.Set((int)Math.Round(cx + dx), (int)Math.Round(cy + dy), color.R, color.G, color.B);
        }
    }

    /// <summary>Волнистая линия через всю ширину.</summary>
    private static void Line(Canvas img, Random rnd, (byte R, byte G, byte B) color, int thickness)
    {
        double y0 = rnd.Next(img.Height), y1 = rnd.Next(img.Height);
        double amp = rnd.Next(3, 12), period = rnd.Next(25, 70), phase = rnd.NextDouble() * Math.PI * 2;
        for (var x = 0; x < img.Width; x++)
        {
            var y = (int)(y0 + (y1 - y0) * x / img.Width + amp * Math.Sin(x / period * Math.PI * 2 + phase));
            for (var t = 0; t < thickness; t++) img.Set(x, y + t, color.R, color.G, color.B);
        }
    }

    private static (byte R, byte G, byte B) RandomColor(Random rnd, int min, int max) =>
        ((byte)rnd.Next(min, max), (byte)rnd.Next(min, max), (byte)rnd.Next(min, max));

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    private sealed class Canvas(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = new byte[width * height * 3];

        public void Set(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            var i = (y * Width + x) * 3;
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
        }
    }

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
