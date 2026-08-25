// Mtrl module — .mtrl material dumper wrapping Lumina's MtrlFile.
// New for Atlas (no xivtool ancestor); acceptance = Lumina MtrlStructs layout + CRC32 name
// recovery (see ShaderNames.cs for the hash rule and verified anchors).
// We parse MtrlFile directly rather than via Lumina.Models.Materials.Material because
// Material.ReadTextures indexes Samplers[i] parallel to TextureOffsets (throws when
// SamplerCount != TextureCount) and Material.ReadStrings under-reads the string table.
// Relative material paths (leading '/') resolve via Material.ResolveRelativeMaterialPath.

using Lumina.Data.Files;

namespace Atlas.Core.Mtrl;

public sealed record MtrlTextureInfo(int Index, string Path, ushort Flags, IReadOnlyList<string> Samplers);

public sealed record MtrlSamplerInfo(uint Id, string? Name, uint Flags, byte TextureIndex, string TexturePath);

public sealed record MtrlConstantInfo(uint Id, string? Name, ushort ValueOffset, ushort ValueSize, float[] Values);

public sealed record MtrlShaderKeyInfo(uint Category, string? CategoryName, uint Value);

public sealed record MtrlInfo(
    string Path,
    string ResolvedPath,
    string ShaderPackage,
    uint Version,
    IReadOnlyList<MtrlTextureInfo> Textures,
    IReadOnlyList<MtrlSamplerInfo> Samplers,
    IReadOnlyList<MtrlConstantInfo> Constants,
    IReadOnlyList<MtrlShaderKeyInfo> ShaderKeys,
    IReadOnlyList<string> UvSets,
    IReadOnlyList<string> ColorSets,
    int ShaderValueCount,
    bool HasColorTable,
    int ColorTableRows,
    int ColorTableWidth,
    bool HasDyeTable,
    int DataSetSize);

public static class MtrlOps
{
    /// <summary>Load and dissect a .mtrl. Accepts absolute game paths and Lumina-style
    /// relative paths (leading '/', resolved with variant 1 chara conventions).</summary>
    public static MtrlInfo Dump(XivEnv env, string path)
    {
        var resolved = path;
        if (path.StartsWith('/'))
            resolved = Lumina.Models.Materials.Material.ResolveRelativeMaterialPath(path, 1)
                       ?? throw new ArgumentException($"cannot resolve relative material path: {path}");

        var mtrl = env.Game.GetFile<MtrlFile>(resolved)
                   ?? throw new FileNotFoundException($"not found: {resolved}");

        var shpk = ReadString(mtrl.Strings, mtrl.FileHeader.ShaderPackageNameOffset);

        var samplers = new List<MtrlSamplerInfo>(mtrl.Samplers.Length);
        var texSamplers = new Dictionary<int, List<string>>();
        foreach (var s in mtrl.Samplers)
        {
            var name = ShaderNames.Resolve(s.SamplerId);
            var texPath = s.TextureIndex < mtrl.TextureOffsets.Length
                ? ReadString(mtrl.Strings, mtrl.TextureOffsets[s.TextureIndex].Offset)
                : "";
            samplers.Add(new MtrlSamplerInfo(s.SamplerId, name, s.Flags, s.TextureIndex, texPath));
            if (!texSamplers.TryGetValue(s.TextureIndex, out var list)) texSamplers[s.TextureIndex] = list = [];
            list.Add(name ?? $"0x{s.SamplerId:X8}");
        }

        var textures = new List<MtrlTextureInfo>(mtrl.TextureOffsets.Length);
        for (var i = 0; i < mtrl.TextureOffsets.Length; i++)
            textures.Add(new MtrlTextureInfo(
                i,
                ReadString(mtrl.Strings, mtrl.TextureOffsets[i].Offset),
                mtrl.TextureOffsets[i].Flags,
                texSamplers.TryGetValue(i, out var list) ? list : []));

        var constants = new List<MtrlConstantInfo>(mtrl.Constants.Length);
        foreach (var c in mtrl.Constants)
        {
            var start = c.ValueOffset / 4;
            var count = c.ValueSize / 4;
            var values = start >= 0 && count >= 0 && start + count <= mtrl.ShaderValues.Length
                ? mtrl.ShaderValues[start..(start + count)]
                : [];
            constants.Add(new MtrlConstantInfo(c.ConstantId, ShaderNames.Resolve(c.ConstantId), c.ValueOffset, c.ValueSize, values));
        }

        var keys = mtrl.ShaderKeys
            .Select(k => new MtrlShaderKeyInfo(k.Category, ShaderNames.Resolve(k.Category), k.Value))
            .ToList();

        var uvSets = mtrl.UvColorSets.Select(u => ReadString(mtrl.Strings, u.NameOffset)).ToArray();
        var colorSets = mtrl.ColorSets.Select(c => ReadString(mtrl.Strings, c.NameOffset)).ToArray();

        // Color table sizing: legacy 512 B = 16 rows x 16 halves; Dawntrail 2048 B = 32 rows x 32
        // halves; anything past the table is the dye block (legacy +32 B, DT +128 B).
        int ds = mtrl.FileHeader.DataSetSize;
        var hasTable = ds > 0;
        int rows = 0, width = 0;
        var hasDye = false;
        if (hasTable)
        {
            if (ds >= 2048) { rows = 32; width = 32; hasDye = ds > 2048; }
            else { rows = 16; width = 16; hasDye = ds > 512; }
        }

        return new MtrlInfo(path, resolved, shpk, mtrl.FileHeader.Version,
            textures, samplers, constants, keys, uvSets, colorSets,
            mtrl.ShaderValues.Length, hasTable, rows, width, hasDye, ds);
    }

    /// <summary>Readable text dump (the `mod Mtrl dump` output).</summary>
    public static void WriteDump(MtrlInfo m, TextWriter w)
    {
        w.WriteLine($"mtrl: {m.Path}");
        if (m.ResolvedPath != m.Path) w.WriteLine($"resolved: {m.ResolvedPath}");
        w.WriteLine($"shpk: {m.ShaderPackage} (version 0x{m.Version:X})");

        w.WriteLine($"textures ({m.Textures.Count}):");
        foreach (var t in m.Textures)
            w.WriteLine($"  [{t.Index}] {t.Path}  flags 0x{t.Flags:X4}" +
                        (t.Samplers.Count > 0 ? $"  <- {string.Join(", ", t.Samplers)}" : ""));

        w.WriteLine($"samplers ({m.Samplers.Count}):");
        foreach (var s in m.Samplers)
            w.WriteLine($"  0x{s.Id:X8} {s.Name ?? "?",-28} flags 0x{s.Flags:X8} -> tex[{s.TextureIndex}] {s.TexturePath}");

        w.WriteLine($"constants ({m.Constants.Count}) of {m.ShaderValueCount * 4} value bytes:");
        foreach (var c in m.Constants)
            w.WriteLine($"  0x{c.Id:X8} {c.Name ?? "?",-28} off {c.ValueOffset,3} size {c.ValueSize,2}  " +
                        $"[{string.Join(", ", c.Values.Select(v => v.ToString("R")))}]");

        w.WriteLine($"shader keys ({m.ShaderKeys.Count}):");
        foreach (var k in m.ShaderKeys)
            w.WriteLine($"  0x{k.Category:X8}{(k.CategoryName != null ? $" {k.CategoryName}" : "")} = 0x{k.Value:X8}");

        if (m.UvSets.Count > 0) w.WriteLine($"uv sets: {string.Join(", ", m.UvSets)}");
        if (m.ColorSets.Count > 0) w.WriteLine($"color sets: {string.Join(", ", m.ColorSets)}");
        w.WriteLine(m.HasColorTable
            ? $"color table: {m.ColorTableRows}x{m.ColorTableWidth} halves ({m.DataSetSize} B dataset{(m.HasDyeTable ? ", +dye" : "")})"
            : "color table: none");
    }

    /// <summary>Null-terminated UTF-8 read at an arbitrary offset into the .mtrl string blob.</summary>
    internal static string ReadString(byte[] blob, int offset)
    {
        if (offset < 0 || offset >= blob.Length) return $"@{offset}";
        var end = Array.IndexOf(blob, (byte)0, offset);
        if (end < 0) end = blob.Length;
        return System.Text.Encoding.UTF8.GetString(blob, offset, end - offset);
    }
}
