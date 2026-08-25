using System.Text;

namespace Atlas.Core.Lgb;

/// <summary>
/// Per-LGB-layer filter ops (raw LGP1/LGB1 parse) — faithful port of CutScan `layerfilters`.
/// Golden: dumps/library/layer-filters.csv. A layer is active under LVB filter key K iff
/// op=None, or Match ∧ K∈keys, or NoMatch ∧ K∉keys.
/// </summary>
public static class LayerFilterOps
{
    public const string Header = "Label,Dir,File,LayerId,LayerName,FilterOp,FilterKeys";

    /// <summary>
    /// LGB file probe order of the original builders (sound/vfx BEFORE planner).
    /// NOT InstanceIdentity.LgbFiles (canonical order) — golden row order depends on this.
    /// </summary>
    public static readonly string[] LgbNames = { "bg", "planmap", "planevent", "planlive", "sound", "vfx", "planner" };

    /// <summary>
    /// Write filter rows for the given dir-list lines (each "bg/.../level\tLABEL"; lines
    /// without a tab skipped). Row order: input line order, then LgbNames order, then layer order.
    /// Returns (files, layers, withFilterKeys).
    /// </summary>
    public static (int Files, int Layers, int Filtered) Write(
        XivEnv env, IEnumerable<string> dirLines, TextWriter w, bool writeHeader = true, Action<string>? log = null)
    {
        if (writeHeader) w.WriteLine(Header);
        int files = 0, layers = 0, filtered = 0;
        foreach (var line in dirLines)
        {
            var tab = line.Split('\t');
            if (tab.Length < 2) continue;
            var dir = tab[0].TrimEnd('/'); var label = tab[1];
            foreach (var lgbName in LgbNames)
            {
                var f = env.Game.GetFile($"{dir}/{lgbName}.lgb");
                if (f == null) continue;
                files++;
                var d = f.Data;
                uint U32(int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
                string CStr(int at) { int e2 = at; while (e2 < d.Length && d[e2] != 0) e2++; return Encoding.ASCII.GetString(d, at, e2 - at); }
                int nSec = (int)U32(8);
                int off = 0xC; int lgp = -1;
                for (int i = 0; i < nSec && off + 8 <= d.Length; i++)
                {
                    var magic = Encoding.ASCII.GetString(d, off, 4);
                    if (magic == "LGP1" || magic == "LGB1") { lgp = off + 8; break; }
                    off += (int)U32(off + 4);
                }
                if (lgp < 0) continue;
                int offLayers = (int)U32(lgp + 8), numLayers = (int)U32(lgp + 0xC);
                for (int k = 0; k < numLayers; k++)
                {
                    int lo = lgp + offLayers + (int)U32(lgp + offLayers + 4 * k);
                    uint layerIdFull = U32(lo);
                    string lname = CStr(lo + (int)U32(lo + 4));
                    int offFilter = (int)U32(lo + 0x14);
                    string op = "None", keys = "";
                    if (offFilter > 0)
                    {
                        int fo = lo + offFilter;
                        uint opv = U32(fo);
                        op = opv == 1 ? "Match" : opv == 2 ? "NoMatch" : "None";
                        int ol = (int)U32(fo + 4), nl = (int)U32(fo + 8);
                        if (nl > 0 && nl < 10000)
                        {
                            var ks = new string[nl];
                            for (int m = 0; m < nl; m++) ks[m] = U32(fo + ol + 4 * m).ToString();
                            keys = string.Join("|", ks);
                            filtered++;
                        }
                    }
                    // NOTE: layer name intentionally NOT escaped (original builder didn't;
                    // golden line 9339 "Phase4,5" is raw). Do not "fix".
                    w.WriteLine($"{label},{dir},{lgbName},{layerIdFull},{lname},{op},{keys}");
                    layers++;
                }
            }
        }
        return (files, layers, filtered);
    }
}
