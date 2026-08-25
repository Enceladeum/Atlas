// Tex module — .tex reader wrapping Lumina's TexFile, PNG export via Tex/PngWriter.cs.
// New for Atlas (no xivtool ancestor); acceptance = known PNG decodes incl. BC formats.
// Lumina decode notes: TexFile.ImageData == TextureBuffer.Filter(mip:0, z:0, B8G8R8A8).RawData,
// i.e. bytes in memory are B,G,R,A (DXGI little-endian C-array order) — we swap to RGBA here.
// Filter() handles L8/A8/B4G4R4A4/B5G5R5A1/B8G8R8A8/B8G8R8X8/R16G16B16A16F/R32G32B32A32F and
// BC1/BC2/BC3/BC5/BC7; BC4 and BC6H land in Lumina's UnsupportedTextureBuffer (no decode).
// TextureFormat is a [Flags] enum with duplicate values (DXT1==BC1...), so Enum.GetName is
// unreliable — format names come from the manual table below.

using Lumina.Data.Files;

namespace Atlas.Core.Tex;

public sealed record TexInfo(
    string Path,
    string Format,
    int FormatValue,
    int Width,
    int Height,
    int Depth,
    int MipCount,
    int ArraySize,
    bool Is1D,
    bool Is2D,
    bool Is3D,
    bool IsCube,
    bool IsArray,
    uint AttributeFlags);

public static class TexOps
{
    /// <summary>Header/format summary for a .tex.</summary>
    public static TexInfo Info(XivEnv env, string path)
    {
        var tex = env.Game.GetFile<TexFile>(path)
                  ?? throw new FileNotFoundException($"not found: {path}");
        var h = tex.Header;
        return new TexInfo(
            path,
            FormatName((int)h.Format),
            (int)h.Format,
            h.Width, h.Height, h.Depth,
            Math.Max(1, h.MipCount),
            h.ArraySize,
            (h.Type & TexFile.Attribute.TextureType1D) != 0,
            (h.Type & TexFile.Attribute.TextureType2D) != 0,
            (h.Type & TexFile.Attribute.TextureType3D) != 0,
            (h.Type & TexFile.Attribute.TextureTypeCube) != 0,
            (h.Type & TexFile.Attribute.TextureType2DArray) != 0,
            (uint)h.Type);
    }

    /// <summary>Decode one mip (slice/face 0) to 8-bit RGBA and write a PNG to the stream.
    /// Returns the mip's pixel dimensions.</summary>
    public static (int Width, int Height) WritePng(XivEnv env, string path, Stream output, int mip = 0)
    {
        var tex = env.Game.GetFile<TexFile>(path)
                  ?? throw new FileNotFoundException($"not found: {path}");
        return WritePng(tex, output, mip);
    }

    /// <summary>Decode one mip of an already-loaded TexFile (slice/face 0) to 8-bit
    /// RGBA and write a PNG to the stream. Returns the mip's pixel dimensions.</summary>
    public static (int Width, int Height) WritePng(TexFile tex, Stream output, int mip = 0)
    {
        var mips = Math.Max(1, tex.Header.MipCount);
        if (mip < 0 || mip >= mips)
            throw new ArgumentOutOfRangeException(nameof(mip), mip, $"texture has {mips} mips");

        var buf = tex.TextureBuffer.Filter(mip: mip, z: 0, format: TexFile.TextureFormat.B8G8R8A8);
        int w = buf.Width, h = buf.Height;
        var bgra = buf.RawData;
        var pixels = w * h;
        if (bgra.Length < pixels * 4)
            throw new InvalidDataException($"decoded buffer too small: {bgra.Length} < {pixels * 4}");

        var rgba = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            rgba[i * 4 + 0] = bgra[i * 4 + 2];
            rgba[i * 4 + 1] = bgra[i * 4 + 1];
            rgba[i * 4 + 2] = bgra[i * 4 + 0];
            rgba[i * 4 + 3] = bgra[i * 4 + 3];
        }

        PngWriter.Write(output, w, h, rgba);
        return (w, h);
    }

    /// <summary>Decode one mip to PNG at a file path (parent dirs created).</summary>
    public static (int Width, int Height) WritePng(XivEnv env, string path, string outPath, int mip = 0)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outPath);
        return WritePng(env, path, fs, mip);
    }

    /// <summary>Stable format names (manual table — the Lumina enum has duplicate values).</summary>
    public static string FormatName(int format) => format switch
    {
        0x1130 => "L8",
        0x1131 => "A8",
        0x1440 => "B4G4R4A4",
        0x1441 => "B5G5R5A1",
        0x1450 => "B8G8R8A8",
        0x1451 => "B8G8R8X8",
        0x1452 => "A8R8G8B82",
        0x2150 => "R32F",
        0x2250 => "R16G16F",
        0x2260 => "R32G32F",
        0x2460 => "R16G16B16A16F",
        0x2470 => "R32G32B32A32F",
        0x3420 => "BC1 (DXT1)",
        0x3430 => "BC2 (DXT3)",
        0x3431 => "BC3 (DXT5)",
        0x6120 => "BC4",
        0x6230 => "BC5 (ATI2)",
        0x6330 => "BC6H",
        0x6432 => "BC7",
        0x4140 => "D16",
        0x4250 => "D24S8",
        0x5100 => "Null",
        0x5140 => "Shadow16",
        0x5150 => "Shadow24",
        _ => $"0x{format:X4}",
    };
}
