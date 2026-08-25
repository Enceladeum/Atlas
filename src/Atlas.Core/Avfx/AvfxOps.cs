// Avfx module — .avfx visual-effect inspection: block tree, summary counts,
// texture references, embedded particle-model export. New for Atlas (no xivtool
// ancestor). Schema reference: Dalamud-VFXEditor (MIT, Formats/AvfxFormat) —
// verified against its reader, not ported wholesale.
//
// Container format: [4-byte reversed-ASCII name][int32 contentSize][content]
// [pad to 4]. The whole file is one top-level "AVFX" block whose payload nests
// the node lists: Schd (schedulers), TmLn (timelines), Emit (emitters),
// Ptcl (particles), Efct (effectors), Bind (binders), Tex (textures: content is
// the path string itself), Modl (embedded models: VDrw 36-byte vertexes —
// half4 pos, byte4 normal(+128), byte4 tangent(+128), byte4 RGBA, 4x half2 UV —
// and VIdx 3x-uint16 triangles).
//
// Inspection is structural: Info counts nodes and resolves texture paths and
// model dims from the known schema; Dump walks the tree heuristically (a block
// recurses only if its payload parses exactly as well-formed child blocks).
// Effect *playback* is out of scope — that is the game's particle engine.

using System.Text;
using Atlas.Core.Gltf;

namespace Atlas.Core.Avfx;

public sealed record AvfxModelInfo(int Index, int VertexCount, int TriCount);

public sealed record AvfxInfo(
    string Path,
    uint Version,
    int Schedulers,
    int Timelines,
    int Emitters,
    int Particles,
    int Effectors,
    int Binders,
    List<string> Textures,
    List<AvfxModelInfo> Models);

public static class AvfxOps
{
    // ---------- low-level block walking ----------

    static string ReadName(byte[] d, int pos)
    {
        Span<char> c = stackalloc char[4];
        var n = 0;
        for (var i = 3; i >= 0; i--)
            if (d[pos + i] != 0) c[n++] = (char)d[pos + i];
        return new string(c[..n]);
    }

    static bool PlausibleName(byte[] d, int pos)
    {
        // Reversed name then zero padding: nonzero alnum bytes from pos, zeros after.
        var seenZero = false;
        var nonZero = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = d[pos + i];
            if (b == 0) { seenZero = true; continue; }
            if (seenZero) return false; // zero in the middle
            var ok = b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
            if (!ok) return false;
            nonZero++;
        }
        return nonZero > 0;
    }

    /// <summary>Enumerate the child blocks of a payload as (name, contentStart, contentSize).
    /// Throws InvalidDataException on malformed structure.</summary>
    internal static IEnumerable<(string Name, int Start, int Size)> Children(byte[] d, int start, int size)
    {
        var pos = start;
        var end = start + size;
        while (pos < end)
        {
            if (pos + 8 > end) throw new InvalidDataException($"truncated block header at 0x{pos:X}");
            var name = ReadName(d, pos);
            var contentSize = BitConverter.ToInt32(d, pos + 4);
            if (contentSize < 0 || pos + 8 + contentSize > end)
                throw new InvalidDataException($"bad block size {contentSize} at 0x{pos:X}");
            yield return (name, pos + 8, contentSize);
            pos += 8 + contentSize;
            pos += contentSize % 4 == 0 ? 0 : 4 - contentSize % 4;
        }
    }

    static bool TryChildren(byte[] d, int start, int size, out List<(string Name, int Start, int Size)> kids)
    {
        kids = new List<(string, int, int)>();
        var pos = start;
        var end = start + size;
        while (pos < end)
        {
            if (pos + 8 > end || !PlausibleName(d, pos)) return false;
            var contentSize = BitConverter.ToInt32(d, pos + 4);
            if (contentSize < 0 || pos + 8 + contentSize > end) return false;
            kids.Add((ReadName(d, pos), pos + 8, contentSize));
            pos += 8 + contentSize;
            pos += contentSize % 4 == 0 ? 0 : 4 - contentSize % 4;
            // Padding may overshoot `end` on the final child only when the
            // parent itself was the last block; require exact consumption.
            if (pos > end) return false;
        }
        return pos == end && kids.Count > 0;
    }

    static byte[] LoadFile(XivEnv env, string path, out int rootStart, out int rootSize)
    {
        var f = env.Game.GetFile(path) ?? throw new FileNotFoundException($"not found: {path}");
        var d = f.Data;
        if (d.Length < 8 || ReadName(d, 0) != "AVFX")
            throw new InvalidDataException($"not an AVFX file: {path}");
        rootSize = BitConverter.ToInt32(d, 4);
        rootStart = 8;
        if (rootSize < 0 || rootStart + rootSize > d.Length)
            throw new InvalidDataException($"bad AVFX root size {rootSize}");
        return d;
    }

    // ---------- info ----------

    public static AvfxInfo Info(XivEnv env, string path)
    {
        var d = LoadFile(env, path, out var start, out var size);
        uint version = 0;
        int schd = 0, tmln = 0, emit = 0, ptcl = 0, efct = 0, bind = 0;
        var textures = new List<string>();
        var models = new List<AvfxModelInfo>();
        foreach (var (name, s, sz) in Children(d, start, size))
        {
            switch (name)
            {
                case "Ver": version = BitConverter.ToUInt32(d, s); break;
                case "Schd": schd++; break;
                case "TmLn": tmln++; break;
                case "Emit": emit++; break;
                case "Ptcl": ptcl++; break;
                case "Efct": efct++; break;
                case "Bind": bind++; break;
                case "Tex":
                    // Tex content is the path string directly (VFXEditor AvfxTexture
                    // hands the whole block to AvfxString), not nested children.
                    textures.Add(Encoding.ASCII.GetString(d, s, sz).TrimEnd('\0'));
                    break;
                case "Modl":
                {
                    int verts = 0, tris = 0;
                    foreach (var (n2, _, z2) in Children(d, s, sz))
                    {
                        if (n2 == "VDrw") verts = z2 / 36;
                        else if (n2 == "VIdx") tris = z2 / 6;
                    }
                    models.Add(new AvfxModelInfo(models.Count, verts, tris));
                    break;
                }
            }
        }
        return new AvfxInfo(path, version, schd, tmln, emit, ptcl, efct, bind, textures, models);
    }

    // ---------- dump (heuristic tree) ----------

    public static IEnumerable<string> Dump(XivEnv env, string path)
    {
        var d = LoadFile(env, path, out var start, out var size);
        yield return $"AVFX  ({size} bytes)";
        foreach (var line in DumpBlocks(d, start, size, 1))
            yield return line;
    }

    static IEnumerable<string> DumpBlocks(byte[] d, int start, int size, int depth)
    {
        if (depth > 16) yield break;
        foreach (var (name, s, sz) in Children(d, start, size))
        {
            var indent = new string(' ', depth * 2);
            if (sz >= 8 && TryChildren(d, s, sz, out _))
            {
                yield return $"{indent}{name}  ({sz} bytes)";
                foreach (var line in DumpBlocks(d, s, sz, depth + 1))
                    yield return line;
            }
            else
            {
                yield return $"{indent}{name}  {LeafPreview(d, s, sz)}";
            }
        }
    }

    static string LeafPreview(byte[] d, int s, int sz)
    {
        if (sz == 0) return "(empty)";
        if (sz == 1) return $"= {d[s]}";
        if (sz == 4)
        {
            var i = BitConverter.ToInt32(d, s);
            var f = BitConverter.ToSingle(d, s);
            return float.IsFinite(f) && MathF.Abs(f) is > 1e-6f and < 1e10f
                ? $"= {i} / {f:0.###}"
                : $"= {i}";
        }
        // Printable NUL-padded string (e.g. Tex Path).
        var printable = 0;
        for (var i = 0; i < sz; i++)
        {
            var b = d[s + i];
            if (b == 0) { if (i == 0) { printable = -1; } break; }
            if (b < 0x20 || b > 0x7e) { printable = -1; break; }
            printable++;
        }
        if (printable > 2)
            return $"= \"{Encoding.ASCII.GetString(d, s, printable)}\"";
        var n = Math.Min(sz, 12);
        var hex = Convert.ToHexString(d, s, n);
        return $"({sz} bytes) {hex}{(sz > n ? "…" : "")}";
    }

    // ---------- embedded model export ----------

    static Half H(byte[] d, int pos) => BitConverter.ToHalf(d, pos);

    /// <summary>Build a GltfWriter holding every embedded model (one mesh+node
    /// each, pastel material, normals + UV1 + indices). Returns model dims.</summary>
    public static List<AvfxModelInfo> BuildModelsGltf(XivEnv env, string path, GltfWriter gltf)
    {
        var d = LoadFile(env, path, out var start, out var size);
        var models = new List<AvfxModelInfo>();
        foreach (var (name, s, sz) in Children(d, start, size))
        {
            if (name != "Modl") continue;
            int vStart = -1, vSize = 0, iStart = -1, iSize = 0;
            foreach (var (n2, s2, z2) in Children(d, s, sz))
            {
                if (n2 == "VDrw") { vStart = s2; vSize = z2; }
                else if (n2 == "VIdx") { iStart = s2; iSize = z2; }
            }
            var vcount = vSize / 36;
            var tris = iSize / 6;
            if (vcount == 0 || tris == 0) { models.Add(new AvfxModelInfo(models.Count, vcount, tris)); continue; }

            var pos = new float[vcount * 3];
            var nrm = new float[vcount * 3];
            var uv = new float[vcount * 2];
            for (var v = 0; v < vcount; v++)
            {
                var b = vStart + v * 36;
                pos[v * 3 + 0] = (float)H(d, b);
                pos[v * 3 + 1] = (float)H(d, b + 2);
                pos[v * 3 + 2] = (float)H(d, b + 4);
                float nx = d[b + 8] - 128, ny = d[b + 9] - 128, nz = d[b + 10] - 128;
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-6f) { nx = 0; ny = 1; nz = 0; len = 1; }
                nrm[v * 3 + 0] = nx / len;
                nrm[v * 3 + 1] = ny / len;
                nrm[v * 3 + 2] = nz / len;
                uv[v * 2 + 0] = (float)H(d, b + 20);
                uv[v * 2 + 1] = (float)H(d, b + 22);
            }
            var idx = new uint[tris * 3];
            var bad = false;
            for (var t = 0; t < tris * 3; t++)
            {
                idx[t] = BitConverter.ToUInt16(d, iStart + t * 2);
                if (idx[t] >= vcount) bad = true;
            }
            if (bad) { models.Add(new AvfxModelInfo(models.Count, vcount, 0)); continue; }

            var mi = models.Count;
            var mat = gltf.AddMaterial($"Modl{mi}", GltfWriter.Pastel($"{path}#{mi}"));
            var mesh = gltf.AddMesh($"Modl{mi}");
            gltf.AddPrimitive(mesh, pos, nrm, uv, idx, mat);
            var node = gltf.AddNode($"Modl{mi}", mesh: mesh);
            gltf.AddSceneRoot(node);
            models.Add(new AvfxModelInfo(mi, vcount, tris));
        }
        return models;
    }
}
