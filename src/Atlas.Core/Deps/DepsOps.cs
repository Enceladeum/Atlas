// Asset dependency index: which materials a model references (mdl -> mtrl) and
// which textures a material references (mtrl -> tex). Together with the
// placement usage index (Territory.UsageOps) this closes the chain
// tex -> mtrl -> mdl -> territories, both directions.
//
// The .mdl side does NOT go through Lumina's MdlFile/Model (v6 chara models
// fail there): material names live in the string blob right after the vertex
// declarations, and the blob layout is stable across v5/v6 —
//   0x44-byte file header | declCount * 136 B declarations |
//   u16 stringCount, u16 pad, u32 stringSize | NUL-separated string blob
// We parse that structurally and validate; if validation fails (unexpected
// version drift), a bounded printable-string scan for ".mtrl" suffixes over
// the head of the file is the fallback. Acceptance: structural result equals
// Lumina's material list on every v5 model where Lumina works.
//
// Chara models store variant-relative material refs ("/mt_...mtrl"); edges
// keep them verbatim, and Query matches an absolute .mtrl path against both
// its full form and "/" + filename. Resolution to a concrete variant path is
// the equipment resolver's job (phase: imc/eqdp), not this index.

using Lumina.Data.Files;

namespace Atlas.Core.Deps;

public sealed record DepEdge(string Kind, string Source, string Target);

public sealed record DepsBuildStats(int Files, int Missing, int Errors, int Edges);

public static class DepsOps
{
    public const string KindMdlMtrl = "MdlMtrl";
    public const string KindMtrlTex = "MtrlTex";
    public const string Header = "Kind,Source,Target";

    /// <summary>Material paths referenced by a raw .mdl byte image (v5 + v6).</summary>
    public static List<string> MdlMaterials(byte[] data)
        => TryStringBlob(data) ?? ScanForMtrl(data, 256 * 1024);

    static List<string>? TryStringBlob(byte[] d)
    {
        if (d.Length < 0x4C) return null;
        var declCount = BitConverter.ToUInt16(d, 12);
        var matCount = BitConverter.ToUInt16(d, 14);
        var off = 0x44L + declCount * 136L;
        if (off + 8 > d.Length) return null;
        var strCount = BitConverter.ToUInt16(d, (int)off);
        var strSize = BitConverter.ToUInt32(d, (int)off + 4);
        var blob = off + 8;
        if (strSize == 0 || strSize > 4 * 1024 * 1024 || blob + strSize > d.Length) return null;

        var mtrls = new List<string>();
        var total = 0;
        var start = (int)blob;
        var end = (int)(blob + strSize);
        var segStart = start;
        for (var i = start; i < end; i++)
        {
            var b = d[i];
            if (b == 0)
            {
                if (i > segStart)
                {
                    total++;
                    if (i - segStart > 5 && d[i - 5] == (byte)'.' && d[i - 4] == (byte)'m'
                        && d[i - 3] == (byte)'t' && d[i - 2] == (byte)'r' && d[i - 1] == (byte)'l')
                        mtrls.Add(System.Text.Encoding.UTF8.GetString(d, segStart, i - segStart));
                }
                segStart = i + 1;
                continue;
            }
            if (b < 0x20 || b > 0x7E) return null; // not a printable-ASCII blob: bail to scan
        }
        // The blob holds bones + materials + attributes + shapes; total strings must
        // match the declared count (trailing padding NULs produce no extra segments).
        if (total != strCount) return null;
        // Soft expectation: .mtrl segments == header MaterialCount. When they disagree
        // the strings are still the ground truth (the header count feeds mesh submesh
        // indexing, not the blob), so return what the blob says.
        _ = matCount;
        return mtrls;
    }

    /// <summary>Fallback: scan the first <paramref name="cap"/> bytes for NUL-terminated
    /// printable strings ending in ".mtrl".</summary>
    public static List<string> ScanForMtrl(byte[] d, int cap)
    {
        var end = Math.Min(d.Length, cap);
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var segStart = -1;
        for (var i = 0; i < end; i++)
        {
            var b = d[i];
            var printable = b >= 0x20 && b <= 0x7E;
            if (printable) { if (segStart < 0) segStart = i; continue; }
            if (b == 0 && segStart >= 0)
            {
                var len = i - segStart;
                if (len is > 6 and < 200 && d[i - 5] == (byte)'.' && d[i - 4] == (byte)'m'
                    && d[i - 3] == (byte)'t' && d[i - 2] == (byte)'r' && d[i - 1] == (byte)'l')
                {
                    var s = System.Text.Encoding.UTF8.GetString(d, segStart, len);
                    if (seen.Add(s)) res.Add(s);
                }
            }
            segStart = -1;
        }
        return res;
    }

    /// <summary>Texture paths referenced by a loaded .mtrl.</summary>
    public static List<string> MtrlTextures(MtrlFile f)
    {
        var res = new List<string>(f.TextureOffsets.Length);
        foreach (var t in f.TextureOffsets)
        {
            var s = ReadZ(f.Strings, t.Offset);
            if (s.Length > 0) res.Add(s);
        }
        return res;
    }

    static string ReadZ(byte[] blob, int offset)
    {
        if (offset < 0 || offset >= blob.Length) return "";
        var end = offset;
        while (end < blob.Length && blob[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(blob, offset, end - offset);
    }

    /// <summary>Sweep .mdl/.mtrl paths and stream Kind,Source,Target rows to
    /// <paramref name="w"/>. Missing files are counted, not fatal (crowdsourced
    /// path lists carry stale entries).</summary>
    public static DepsBuildStats BuildIndex(XivEnv env, IEnumerable<string> paths, TextWriter w,
        bool header = true, Action<string>? log = null)
    {
        if (header) w.WriteLine(Header);
        int files = 0, miss = 0, err = 0, edges = 0;
        foreach (var p in paths)
        {
            files++;
            try
            {
                if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    var f = env.Game.GetFile(p);
                    if (f == null) { miss++; }
                    else foreach (var m in MdlMaterials(f.Data))
                    { w.WriteLine($"{KindMdlMtrl},{Csv.Escape(p)},{Csv.Escape(m)}"); edges++; }
                }
                else if (p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                {
                    var f = env.Game.GetFile<MtrlFile>(p);
                    if (f == null) { miss++; }
                    else foreach (var t in MtrlTextures(f))
                    { w.WriteLine($"{KindMtrlTex},{Csv.Escape(p)},{Csv.Escape(t)}"); edges++; }
                }
            }
            catch { err++; }
            if (log != null && files % 20000 == 0)
                log($"{files} files, {edges} edges ({miss} missing, {err} errors)");
        }
        return new DepsBuildStats(files, miss, err, edges);
    }

    /// <summary>Both directions for one asset path from a built index CSV.
    /// Streaming scan — no full load. Comparisons ordinal-ignore-case; an
    /// absolute .mtrl path additionally matches its "/"+filename relative form
    /// (chara model refs).</summary>
    public static (List<DepEdge> Uses, List<DepEdge> UsedBy) Query(string indexCsv, string assetPath)
    {
        var uses = new List<DepEdge>();
        var usedBy = new List<DepEdge>();
        var alt = assetPath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) && !assetPath.StartsWith('/')
            ? "/" + Path.GetFileName(assetPath) : null;
        foreach (var line in File.ReadLines(indexCsv))
        {
            // Rows are Kind,Source,Target; game paths never contain commas/quotes.
            var c1 = line.IndexOf(',');
            if (c1 < 0) continue;
            var c2 = line.IndexOf(',', c1 + 1);
            if (c2 < 0) continue;
            var kind = line[..c1];
            if (kind == "Kind") continue;
            var source = line[(c1 + 1)..c2];
            var target = line[(c2 + 1)..];
            if (source.Equals(assetPath, StringComparison.OrdinalIgnoreCase))
                uses.Add(new DepEdge(kind, source, target));
            if (target.Equals(assetPath, StringComparison.OrdinalIgnoreCase)
                || (alt != null && target.Equals(alt, StringComparison.OrdinalIgnoreCase)))
                usedBy.Add(new DepEdge(kind, source, target));
        }
        return (uses, usedBy);
    }

    /// <summary>Read a path list (.txt or .gz), one path per line.</summary>
    public static IEnumerable<string> ReadPathsFile(string file)
    {
        using var raw = File.OpenRead(file);
        using var stream = file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new StreamReader(new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress))
            : new StreamReader(raw);
        while (stream.ReadLine() is { } line)
        {
            var t = line.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    /// <summary>Filter to the extensions the index covers.</summary>
    public static IEnumerable<string> IndexablePaths(IEnumerable<string> paths)
        => paths.Where(p => p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase));
}
