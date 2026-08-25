using System.Text;
using System.Text.RegularExpressions;
using Lumina;

namespace Atlas.Core.Sgb;

/// <summary>
/// SGB (shared group) layouts census — faithful port of CutScan `sgblayouts`
/// (pipelines/CutScan/Program.cs, dispatch ~line 262).
///
/// SGB = same FileHeader/SCN1 container as LVB. FileSceneHeader.OffsetEmbeddedLayerGroups
/// @+0 / Num @+4 -> FileLayerGroupHeader[] (0x10 each, offsets self-relative) -> layers ->
/// instances (Type @0, Key @4, transform 9 floats @+0xC LOCAL-space; path offset @+0x30
/// for BgPart/SharedGroup/HelperObject). Golden: dumps/library/sgb-layouts.csv.
///
/// NOTE (golden schema wins): layer/instance names have commas replaced by ';' instead of
/// CSV quoting — that is what the golden file contains; do not "improve" to Csv.Escape.
/// NOTE: the golden predates CutScan's Vfx-path extraction (added later for
/// mapvfx-census): golden AssetPath is EMPTY for Type=4 Vfx rows. Default output
/// matches the golden; pass vfxPaths=true (CLI --vfx-paths) for the newer behavior
/// (avfx path @ io+0x30 for Vfx rows too).
/// </summary>
public static class SgbLayouts
{
    public const string Header =
        "Sgb,LayerGroupId,LayerKey,LayerName,InstKey,Type,TypeName,InstName,AssetPath," +
        "X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ";

    static readonly Dictionary<uint, string> TypeNames = new()
    {
        { 1, "BgPart" }, { 2, "Attribute" }, { 3, "Light" }, { 4, "Vfx" },
        { 5, "PositionMarker" }, { 6, "SharedGroup" }, { 7, "Sound" }, { 8, "EventNpc" },
        { 9, "BattleNpc" }, { 12, "Aetheryte" }, { 14, "Gathering" }, { 15, "HelperObject" },
        { 16, "Treasure" }, { 17, "Clip" }, { 36, "CutAssetOnlySelectable" }, { 40, "PopRange" },
        { 41, "ExitRange" }, { 43, "MapRange" }, { 44, "NaviMeshRange" }, { 45, "EventObject" },
        { 47, "EnvLocation" }, { 48, "ControlPoint" }, { 49, "EventRange" }, { 52, "Timeline" },
        { 57, "CollisionBox" }, { 58, "DoorRange" }, { 59, "LineVfx" },
    };

    public sealed class Stats
    {
        public int Files;    // sgb files parsed
        public int Missing;  // paths absent from sqpack
        public int Bad;      // no SCN1 section / parse exception
        public int Rows;     // instance rows written
    }

    /// <summary>
    /// Reproduces the golden SGB enumeration: distinct union of
    /// (a) instances.csv `Asset=...sgb` references and (b) scene-parts.csv prop
    /// paths ending in .sgb, ordinal-sorted. 10,103 paths against the golden inputs.
    /// </summary>
    public static List<string> BuildPathList(string instancesCsv, string scenePartsCsv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var rx = new Regex(@"Asset=([^;,""]*\.sgb)", RegexOptions.Compiled);
        foreach (var line in File.ReadLines(instancesCsv))
            foreach (Match m in rx.Matches(line))
                set.Add(m.Groups[1].Value);
        var first = true;
        foreach (var line in File.ReadLines(scenePartsCsv))
        {
            if (first) { first = false; continue; }   // header: CutsceneRow,Cutb,Kind,Path
            var parts = line.Split(',');
            if (parts.Length >= 4 && parts[3].EndsWith(".sgb", StringComparison.Ordinal))
                set.Add(parts[3]);
        }
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// Emit layout rows for paths[lo..hi) to <paramref name="w"/> (rows only — the caller
    /// owns the header, see <see cref="Header"/>). Row order == path-list order == golden order.
    /// </summary>
    public static Stats Run(GameData gd, IReadOnlyList<string> paths, TextWriter w,
        int lo = 0, int hi = int.MaxValue, bool vfxPaths = false, Action<string>? log = null)
    {
        var stats = new Stats();
        for (int pi = lo; pi < hi && pi < paths.Count; pi++)
        {
            var p = paths[pi].Trim();
            if (p.Length == 0) continue;
            var f = gd.GetFile(p);
            if (f == null) { stats.Missing++; continue; }
            stats.Files++;
            var d = f.Data;
            uint U32(int at) => at + 4 <= d.Length ? (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24) : 0;
            float F32(int at) => BitConverter.ToSingle(d, at);
            string CStr(int at)
            {
                if (at <= 0 || at >= d.Length) return "";
                int e = at;
                while (e < d.Length && d[e] != 0) e++;
                return Encoding.UTF8.GetString(d, at, e - at);
            }
            try
            {
                int nSec = (int)U32(8);
                int off = 0xC; int scn = -1;
                for (int i = 0; i < nSec && off + 8 <= d.Length; i++)
                {
                    var magic = Encoding.ASCII.GetString(d, off, 4);
                    if (magic == "SCN1") { scn = off + 8; break; }
                    off += (int)U32(off + 4);
                }
                if (scn < 0) { stats.Bad++; continue; }
                int offEmb = (int)U32(scn), numEmb = (int)U32(scn + 4);
                for (int g = 0; g < numEmb; g++)
                {
                    int lg = scn + offEmb + g * 0x10;
                    uint lgId = U32(lg);
                    int offLayers = (int)U32(lg + 8), numLayers = (int)U32(lg + 0xC);
                    for (int k = 0; k < numLayers; k++)
                    {
                        int lyr = lg + offLayers + (int)U32(lg + offLayers + 4 * k);
                        uint lkey = (uint)(d[lyr] | d[lyr + 1] << 8);
                        string lname = CStr(lyr + (int)U32(lyr + 4)).Replace(",", ";");
                        int offInst = (int)U32(lyr + 8), numInst = (int)U32(lyr + 0xC);
                        for (int j = 0; j < numInst; j++)
                        {
                            int io = lyr + offInst + (int)U32(lyr + offInst + 4 * j);
                            uint ty = U32(io); uint ikey = U32(io + 4);
                            int onm = (int)U32(io + 8);
                            string iname = onm > 0 ? CStr(io + onm).Replace(",", ";") : "";
                            string asset = "";
                            if (ty == 1 || ty == 6 || ty == 15 || (vfxPaths && ty == 4))
                            {
                                int po = (int)U32(io + 0x30);
                                if (po > 0) asset = CStr(io + po);
                            }
                            w.WriteLine(
                                $"{p},{lgId},{lkey},{lname},{ikey},{ty}," +
                                $"{(TypeNames.TryGetValue(ty, out var tn) ? tn : ty.ToString())},{iname},{asset}," +
                                $"{Csv.Str(F32(io + 0xC))},{Csv.Str(F32(io + 0x10))},{Csv.Str(F32(io + 0x14))}," +
                                $"{Csv.Str(F32(io + 0x18))},{Csv.Str(F32(io + 0x1C))},{Csv.Str(F32(io + 0x20))}," +
                                $"{Csv.Str(F32(io + 0x24))},{Csv.Str(F32(io + 0x28))},{Csv.Str(F32(io + 0x2C))}");
                            stats.Rows++;
                        }
                    }
                }
            }
            catch { stats.Bad++; }
        }
        log?.Invoke($"files {stats.Files} missing {stats.Missing} bad {stats.Bad} rows {stats.Rows}");
        return stats;
    }
}
