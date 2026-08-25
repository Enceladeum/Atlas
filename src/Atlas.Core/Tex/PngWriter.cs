// Minimal hand-rolled PNG writer: 8-bit RGBA, filter 0 per scanline, zlib IDAT via
// System.IO.Compression.ZLibStream (proper zlib header + adler32). No packages (BCL only).
// PNG chunk CRC is the STANDARD CRC32 (init 0xFFFFFFFF, final xor 0xFFFFFFFF) — distinct
// from the shpk name hash in Mtrl/ShaderNames.cs which uses init 0 / no xor.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Atlas.Core.Tex;

public static class PngWriter
{
    /// <summary>Write an 8-bit RGBA PNG (color type 6). rgba length must be width*height*4.</summary>
    public static void Write(Stream s, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"bad dimensions {width}x{height}");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"need {width * height * 4} bytes, got {rgba.Length}", nameof(rgba));

        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: truecolor + alpha
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter method
        ihdr[12] = 0;  // interlace
        WriteChunk(s, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = width * 4;
                for (var y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter type 0 (None)
                    z.Write(rgba.Slice(y * stride, stride));
                }
            }
            WriteChunk(s, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(s, "IEND", []);
    }

    /// <summary>Convenience: write to a file path (parent dirs created).</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        Write(fs, width, height, rgba);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes);
        s.Write(data);

        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
