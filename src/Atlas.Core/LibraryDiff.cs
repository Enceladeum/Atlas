namespace Atlas.Core;

/// <summary>
/// Patch-drift guard: keyed row compare between two library directories
/// (e.g. dumps/library vs a fresh `Library rebuild` output). Generalizes the
/// Wave 3 drift methodology (dumps/library-rebuild-20260803.md) into a
/// repeatable command so nothing drifts silently across game patches.
///
/// Per file: rows are grouped by a key (natural key columns where the file
/// has one, whole-row multiset where it doesn't); keys only in B are ADDED,
/// only in A are REMOVED, in both with different row multisets are CHANGED.
/// Line endings are normalized (goldens were CRLF, ports write LF).
/// Core policy: no Console, no env vars — TextWriter + log callback only.
/// </summary>
public static class LibraryDiff
{
    /// <param name="KeyCols">CSV column indices forming the row key;
    /// empty = whole-line multiset compare. Null Header = headerless file.</param>
    public sealed record FileSpec(string Target, string FileName, int[] KeyCols, bool HasHeader = true);

    /// <summary>Canonical library files, same target names as Library rebuild.</summary>
    public static readonly FileSpec[] Files =
    [
        new("sets",       "layer-sets.csv",                  [1, 2, 3]),    // Dir,LvbPath,FilterIndex
        new("filters",    "layer-filters.csv",               [1, 2, 3]),    // Dir,File,LayerId
        new("layers",     "layers.csv",                      [1, 2, 3]),    // Dir,File,LayerId
        new("instances",  "instances.csv",                   [1, 2, 3, 5]), // Dir,File,LayerId,InstanceId (identity rule)
        new("props",      "cutscene-prop-transforms.csv",    [0, 1, 2]),    // CutsceneRow,Cutb,EntryId
        new("keyframes",  "cutscene-timeline-keyframes.csv", [0, 1, 2, 3]), // CutsceneRow,Cutb,TmlbIdx,Handle
        new("sceneparts", "scene-parts.csv",                 []),           // no natural key: whole-row multiset
        new("sgbpaths",   "sgb-paths.txt",                   [], HasHeader: false),
        new("sgblayouts", "sgb-layouts.csv",                 [0, 1, 2, 4]), // Sgb,LayerGroupId,LayerKey,InstKey
    ];

    public sealed class FileResult
    {
        public required string FileName;
        public int RowsA, RowsB, Added, Removed, Changed;
        public bool MissingA, MissingB, HeaderChanged;
        public List<string> AddedKeys = new(), RemovedKeys = new(), ChangedKeys = new();
        public bool Identical => !MissingA && !MissingB && !HeaderChanged
                                 && Added == 0 && Removed == 0 && Changed == 0;
    }

    /// <summary>Diff dirA (old) vs dirB (new). Returns per-file results for the
    /// selected targets; files missing from BOTH dirs are skipped with a log line.</summary>
    public static List<FileResult> Run(string dirA, string dirB, TextWriter report,
        IEnumerable<string>? onlyTargets = null, int maxExamples = 5, Action<string>? log = null)
    {
        var only = onlyTargets?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<FileResult>();
        report.WriteLine($"# library diff");
        report.WriteLine($"#   A (old): {dirA}");
        report.WriteLine($"#   B (new): {dirB}");
        report.WriteLine();

        foreach (var spec in Files)
        {
            if (only != null && !only.Contains(spec.Target)) continue;
            var pa = Path.Combine(dirA, spec.FileName);
            var pb = Path.Combine(dirB, spec.FileName);
            var ea = File.Exists(pa);
            var eb = File.Exists(pb);
            if (!ea && !eb) { log?.Invoke($"# {spec.FileName}: missing in both dirs, skipped"); continue; }

            var r = new FileResult { FileName = spec.FileName, MissingA = !ea, MissingB = !eb };
            results.Add(r);
            if (!ea || !eb)
            {
                report.WriteLine($"== {spec.FileName}: MISSING in {(ea ? "B" : "A")}");
                report.WriteLine();
                continue;
            }

            string? headerA = null, headerB = null;
            var a = Load(pa, spec, ref headerA);
            var b = Load(pb, spec, ref headerB);
            r.RowsA = a.Values.Sum(v => v.Count);
            r.RowsB = b.Values.Sum(v => v.Count);
            r.HeaderChanged = spec.HasHeader && headerA != headerB;

            foreach (var (key, rowsA) in a)
            {
                if (!b.TryGetValue(key, out var rowsB))
                {
                    r.Removed += rowsA.Count;
                    Example(r.RemovedKeys, key, maxExamples);
                }
                else if (!SameMultiset(rowsA, rowsB))
                {
                    r.Changed++;
                    Example(r.ChangedKeys, key, maxExamples);
                }
            }
            foreach (var (key, rowsB) in b)
                if (!a.ContainsKey(key))
                {
                    r.Added += rowsB.Count;
                    Example(r.AddedKeys, key, maxExamples);
                }

            report.WriteLine($"== {spec.FileName}: rowsA={r.RowsA} rowsB={r.RowsB}  " +
                             (r.Identical ? "IDENTICAL" : $"added={r.Added} removed={r.Removed} changed={r.Changed}" +
                                                          (r.HeaderChanged ? " HEADER-CHANGED" : "")));
            if (r.HeaderChanged)
            {
                report.WriteLine($"   headerA: {headerA}");
                report.WriteLine($"   headerB: {headerB}");
            }
            WriteExamples(report, "+", r.AddedKeys, r.Added);
            WriteExamples(report, "-", r.RemovedKeys, r.Removed);
            WriteExamples(report, "~", r.ChangedKeys, r.Changed);
            report.WriteLine();
        }

        var dirty = results.Count(x => !x.Identical);
        report.WriteLine(dirty == 0
            ? $"# CLEAN: {results.Count} file(s) identical"
            : $"# DRIFT: {dirty} of {results.Count} file(s) differ");
        report.Flush();
        return results;
    }

    static void Example(List<string> list, string key, int max)
    {
        if (list.Count < max) list.Add(key.Replace(Sep, '|'));
    }

    static void WriteExamples(TextWriter w, string tag, List<string> keys, int total)
    {
        foreach (var k in keys) w.WriteLine($"   {tag} {k}");
        if (total > keys.Count && keys.Count > 0) w.WriteLine($"   {tag} ... ({total - keys.Count} more)");
    }

    const char Sep = '\x1f';

    static bool SameMultiset(List<string> x, List<string> y)
    {
        if (x.Count != y.Count) return false;
        if (x.Count == 1) return x[0] == y[0];
        x.Sort(StringComparer.Ordinal); y.Sort(StringComparer.Ordinal);
        for (var i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
        return true;
    }

    static Dictionary<string, List<string>> Load(string path, FileSpec spec, ref string? header)
    {
        var map = new Dictionary<string, List<string>>();
        var first = spec.HasHeader;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw; // CRLF golden vs LF port
            if (first) { header = line; first = false; continue; }
            if (line.Length == 0) continue;
            string key;
            if (spec.KeyCols.Length == 0) key = line;
            else
            {
                var f = SplitCsv(line);
                var parts = new string[spec.KeyCols.Length];
                for (var i = 0; i < spec.KeyCols.Length; i++)
                    parts[i] = spec.KeyCols[i] < f.Count ? f[spec.KeyCols[i]] : "";
                key = string.Join(Sep, parts);
            }
            if (!map.TryGetValue(key, out var rows)) map[key] = rows = new List<string>(1);
            rows.Add(line);
        }
        return map;
    }

    /// <summary>Minimal RFC-4180 field splitter (quotes, doubled quotes, commas).</summary>
    public static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
