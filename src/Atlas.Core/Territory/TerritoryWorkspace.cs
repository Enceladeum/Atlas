using Lumina.Data;
using Lumina.Excel;
using Atlas.Core.Exd;
using Atlas.Core.Lgb;
using Atlas.Core.Lvb;
using Atlas.Core.Pcb;

namespace Atlas.Core.Territory;

/// <summary>
/// Territory workspace v1: one folder of per-territory research tables for a
/// TerritoryType row — live single-dir runs of the library builders (exact row
/// subsets of dumps/library), the schematized TerritoryType row, layer-activation
/// axes, ENPC/exit/VFX/CFC joins, and quest-NPC slices of the library manifest.
/// See NOTES-Territory.md for design decisions and deferred items.
/// Core policy: no Console, no env vars — paths + TextWriter + log callback only.
/// </summary>
public static class TerritoryWorkspace
{
    public sealed class Result
    {
        public uint TerritoryId;
        public string LevelDir = "", Label = "", Name = "", Place = "";
        /// <summary>file name -> data-row count (header excluded)</summary>
        public Dictionary<string, int> Counts = new();
    }

    // ---------- named-column sheet access (schema names with raw-index fallback) ----------

    sealed class NamedSheet
    {
        public required RawExcelSheet Raw;
        public required ExcelSheet<RawRow> Rows;
        public SchemaNames? Schema;
        readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

        public static NamedSheet Open(XivEnv env, string sheet, Action<string>? log)
        {
            RawExcelSheet raw;
            try { raw = env.Game.Excel.GetRawSheet(sheet, Language.English); }
            catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { raw = env.Game.Excel.GetRawSheet(sheet, Language.None); }
            var ns = new NamedSheet
            {
                Raw = raw,
                Rows = env.Game.Excel.GetSheet<RawRow>(raw.Language, sheet),
                Schema = SchemaNames.TryLoad(env.SchemaDir, sheet, raw, log),
            };
            if (ns.Schema != null)
                for (var k = 0; k < ns.Schema.Names.Length; k++)
                    ns._byName[ns.Schema.Names[k]] = ns.Schema.OffsetOrder[k];
            else
                log?.Invoke($"# warning: no EXDSchema for {sheet}; named columns fall back to fixed raw indices (patch-fragile)");
            return ns;
        }

        /// <summary>Raw column index for a schema name; fallbackRaw when the schema is absent
        /// or lacks the name (-1 = no fallback).</summary>
        public int Col(string name, int fallbackRaw = -1) =>
            _byName.TryGetValue(name, out var i) ? i : fallbackRaw;

        public string Str(RawRow row, string name, int fallbackRaw = -1)
        {
            var c = Col(name, fallbackRaw);
            return c >= 0 && c < Raw.Columns.Count ? Csv.Str(row.ReadColumn(c)) : "";
        }
    }

    // ---------- entry point ----------

    /// <summary>Build the workspace for one territory. libraryDir must contain
    /// quest-npc-manifest.csv + instances.csv (CLI validates before calling).</summary>
    public static Result Run(XivEnv env, uint territoryId, string outDir, string libraryDir,
        bool collision = false, Action<string>? log = null)
    {
        Directory.CreateDirectory(outDir);
        var res = new Result { TerritoryId = territoryId };
        var ttStr = territoryId.ToString();

        // --- resolve TerritoryType row + Bg -> level dir ---
        var ttSheet = NamedSheet.Open(env, "TerritoryType", log);
        if (!ttSheet.Rows.HasRow(territoryId)) throw new Exception($"TerritoryType {territoryId} not found");
        var ttRow = ttSheet.Rows.GetRow(territoryId);

        var bg = ttSheet.Str(ttRow, "Bg", 1);
        if (!bg.Contains('/'))
        {
            // heuristic fallback (mirrors Program.cs `lgb`): first string column with /level/,
            // else first with >=3 slashes
            bg = "";
            for (var c = 0; c < ttSheet.Raw.Columns.Count; c++)
                if (ttSheet.Raw.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String)
                {
                    var s = Csv.Str(ttRow.ReadColumn(c));
                    if (s.Contains("/level/")) { bg = s; break; }
                    if (bg.Length == 0 && s.Count(ch => ch == '/') >= 3) bg = s;
                }
        }
        if (!bg.Contains('/')) throw new Exception($"no Bg path on TerritoryType {territoryId}");
        var levelDir = "bg/" + bg[..bg.LastIndexOf('/')];
        res.LevelDir = levelDir;
        res.Name = ttSheet.Str(ttRow, "Name", 0);
        var intendedUse = ttSheet.Str(ttRow, "TerritoryIntendedUse", 9);

        // PlaceName join
        var placeSheet = NamedSheet.Open(env, "PlaceName", log);
        if (uint.TryParse(ttSheet.Str(ttRow, "PlaceName", 5), out var pnId) && placeSheet.Rows.HasRow(pnId))
            res.Place = placeSheet.Str(placeSheet.Rows.GetRow(pnId), "Name", 0);

        // --- label: must match dumps/library so live slices are byte-identical subsets.
        // Shared level dirs mean the library label is not always TT<this tt> (e.g. Mist 339
        // lives in s1h1 labeled TT136) — look it up from the library layer-sets.csv.
        res.Label = "TT" + ttStr;
        var setsLib = Path.Combine(libraryDir, "layer-sets.csv");
        if (File.Exists(setsLib))
            foreach (var line in File.ReadLines(setsLib).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length > 2 && p[1] == levelDir) { res.Label = p[0]; break; }
            }
        log?.Invoke($"# territory {territoryId} '{res.Place}' dir={levelDir} label={res.Label}");

        string Out(string f) => Path.Combine(outDir, f);

        // --- 2. territorytype.csv (schematized Name,Value) ---
        using (var w = Csv.OpenWriter(Out("territorytype.csv")))
        {
            w.WriteLine("Name,Value");
            w.WriteLine($"RowId,{territoryId}");
            var n = 1;
            if (ttSheet.Schema != null)
                for (var k = 0; k < ttSheet.Schema.Names.Length; k++, n++)
                    w.WriteLine($"{Csv.Escape(ttSheet.Schema.Names[k])},{Csv.Escape(Csv.Str(ttRow.ReadColumn(ttSheet.Schema.OffsetOrder[k])))}");
            else
                for (var c = 0; c < ttSheet.Raw.Columns.Count; c++, n++)
                    w.WriteLine($"col{c},{Csv.Escape(Csv.Str(ttRow.ReadColumn(c)))}");
            res.Counts["territorytype.csv"] = n;
        }

        // --- 3. live single-dir library-builder runs (exact library row subsets) ---
        var dirLines = new List<string> { $"{levelDir}\t{res.Label}" };
        using (var w = Csv.OpenWriter(Out("layer-sets.csv")))
            res.Counts["layer-sets.csv"] = LvbOps.WriteLayerSets(env, dirLines, w, true, log).Filters;
        using (var w = Csv.OpenWriter(Out("layer-filters.csv")))
            res.Counts["layer-filters.csv"] = LayerFilterOps.Write(env, dirLines, w, true, log).Layers;
        using (var w = Csv.OpenWriter(Out("layers.csv")))
            res.Counts["layers.csv"] = LayerCensus.Write(env, dirLines, w, true, log).Layers;
        using (var w = Csv.OpenWriter(Out("instances.csv")))
            res.Counts["instances.csv"] = InstanceCensus.Write(env, dirLines, w, true, log).Instances;

        // --- parse the freshly written slices for the derived tables ---
        var layerRows = File.ReadLines(Out("layers.csv")).Skip(1).Select(LibraryDiff.SplitCsv).ToList();
        var instLines = File.ReadLines(Out("instances.csv")).Skip(1).ToList();
        var instRows = instLines.Select(LibraryDiff.SplitCsv).ToList();
        var setRows = File.ReadLines(Out("layer-sets.csv")).Skip(1).Select(l => l.Split(',')).ToList();

        // layer-filters.csv is written UNescaped (golden quirk: raw commas in layer names) —
        // parse end-anchored: last field = FilterKeys, second-last = FilterOp.
        var filterByLayer = new Dictionary<(string File, string LayerId), (string Op, string Keys)>();
        foreach (var line in File.ReadLines(Out("layer-filters.csv")).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 7) continue;
            filterByLayer[(p[2], p[3])] = (p[^2], p[^1]);
        }

        // --- 4. layer-axes.csv ---
        using (var w = Csv.OpenWriter(Out("layer-axes.csv")))
        {
            w.WriteLine("Dir,File,LayerId,LayerName,Axis,Detail");
            var n = 0;
            foreach (var f in layerRows)
            {
                // Label0 Dir1 File2 LayerId3 LayerName4 FestivalID5 FestivalPhase6 IsTemporary7
                // IsHousing8 InstanceCount9 BgPartCount10 LayerSetIds11
                filterByLayer.TryGetValue((f[2], f[3]), out var flt);
                string axis, detail;
                if (!string.IsNullOrEmpty(flt.Keys)) { axis = "FilterKey"; detail = $"op={flt.Op};keys={flt.Keys}"; }
                else if (f[5] != "0") { axis = "Festival"; detail = $"festival={f[5]};phase={f[6]}"; }
                // NOTE: IsTemporary is 0 for every layer in the current game data, so this
                // axis never fires from the flag; server-toggled quest layers are file-side
                // indistinguishable from Always (see NOTES-Territory.md).
                else if (f[7] == "True" || f[7] == "1") { axis = "ServerToggle"; detail = $"IsTemporary={f[7]}"; }
                else { axis = "Always"; detail = ""; }

                // layer-set keys referencing this layer, via the (known-buggy, see NOTES)
                // layers.csv LayerSetIds joined to layer-sets FilterIndex/Key
                var setKeys = new List<string>();
                foreach (var id in f[11].Split('|', StringSplitOptions.RemoveEmptyEntries))
                    foreach (var s in setRows)
                        if ((id == s[4] || id == s[3]) && !setKeys.Contains(s[4]))
                            setKeys.Add(s[4]);
                if (setKeys.Count > 0)
                    detail += (detail.Length > 0 ? ";" : "") + $"sets=[{string.Join("|", setKeys)}]";

                w.WriteLine($"{f[1]},{f[2]},{f[3]},{Csv.Escape(f[4])},{axis},{Csv.Escape(detail)}");
                n++;
            }
            res.Counts["layer-axes.csv"] = n;
            if (n != layerRows.Count) log?.Invoke($"# warning: layer-axes rows {n} != layers rows {layerRows.Count}");
        }

        // --- 5. npcs.csv (ENPC instances joined to ENpcResident.Singular) ---
        NamedSheet? enpc = null;
        using (var w = Csv.OpenWriter(Out("npcs.csv")))
        {
            w.WriteLine("BaseId,Name,LgbFile,LayerId,InstanceId,X,Y,Z");
            var n = 0;
            foreach (var f in instRows)
            {
                // Label0 Dir1 File2 LayerId3 AssetType4 InstanceId5 Name6 X7 Y8 Z9 ... Extra16
                if (f[4] != "EventNPC") continue;
                var baseId = ExtraValue(f[16], "BaseId");
                var name = "";
                if (uint.TryParse(baseId, out var bid))
                {
                    enpc ??= NamedSheet.Open(env, "ENpcResident", log);
                    if (enpc.Rows.HasRow(bid)) name = enpc.Str(enpc.Rows.GetRow(bid), "Singular", 0);
                }
                w.WriteLine($"{baseId},{Csv.Escape(name)},{f[2]},{f[3]},{f[5]},{f[7]},{f[8]},{f[9]}");
                n++;
            }
            res.Counts["npcs.csv"] = n;
        }

        // --- 6. quest-npcs.csv (row-exact manifest slice, TerritoryId == tt) ---
        var manifest = Path.Combine(libraryDir, "quest-npc-manifest.csv");
        var questSlice = new List<List<string>>();
        using (var w = Csv.OpenWriter(Out("quest-npcs.csv")))
        {
            var first = true;
            var n = 0;
            foreach (var line in File.ReadLines(manifest))
            {
                if (first) { w.WriteLine(line); first = false; continue; }
                var f = LibraryDiff.SplitCsv(line);
                if (f.Count > 8 && f[8] == ttStr)
                {
                    w.WriteLine(line); // byte-identical row
                    questSlice.Add(f);
                    n++;
                }
            }
            res.Counts["quest-npcs.csv"] = n;
        }

        // --- 7. quest-policy.csv (v1 consumer table; deep seq resolution DEFERRED) ---
        NamedSheet? quest = null;
        using (var w = Csv.OpenWriter(Out("quest-policy.csv")))
        {
            w.WriteLine("QuestId,QuestName,PreviousQuests,ActorCount,Actors");
            var n = 0;
            foreach (var g in questSlice.GroupBy(f => f[0])) // first-appearance order preserved
            {
                var qname = g.First()[1];
                var actors = new List<string>();
                foreach (var f in g)
                    if (f[6] == "Actor" && f[7].Length > 0 && !actors.Contains(f[7]))
                        actors.Add(f[7]);
                var prev = new List<string>();
                if (uint.TryParse(g.Key, out var qid))
                {
                    quest ??= NamedSheet.Open(env, "Quest", log);
                    if (quest.Rows.HasRow(qid))
                    {
                        var qrow = quest.Rows.GetRow(qid);
                        int[] fallback = [9, 11, 12]; // Quest PreviousQuest[0..2] raw indices, 7.55
                        for (var i = 0; ; i++)
                        {
                            var c = quest.Col($"PreviousQuest[{i}]", i < fallback.Length ? fallback[i] : -1);
                            if (c < 0) break;
                            var v = Csv.Str(qrow.ReadColumn(c));
                            if (v != "0" && v.Length > 0) prev.Add(v);
                        }
                    }
                }
                w.WriteLine($"{g.Key},{Csv.Escape(qname)},{Csv.Escape(string.Join("|", prev))},{actors.Count},{Csv.Escape(string.Join("|", actors))}");
                n++;
            }
            res.Counts["quest-policy.csv"] = n;
        }

        // --- 8. exits.csv (out = this slice's ExitRanges; in = library ExitRanges targeting tt) ---
        var outEdges = new List<string>();
        var inEdges = new List<string>();
        using (var w = Csv.OpenWriter(Out("exits.csv")))
        {
            w.WriteLine("Direction,Dir,File,LayerId,InstanceId,ExitType,OtherTerritory,Index,X,Y,Z");
            var exitRanges = 0;
            foreach (var f in instRows)
            {
                if (f[4] != "ExitRange") continue;
                exitRanges++;
                var dest = ExtraValue(f[16], "TerritoryType");
                w.WriteLine($"out,{f[1]},{f[2]},{f[3]},{f[5]},{ExtraValue(f[16], "ExitType")},{dest},{ExtraValue(f[16], "Index")},{f[7]},{f[8]},{f[9]}");
                if (dest.Length > 0) outEdges.Add(dest);
            }
            if (outEdges.Count != exitRanges)
                log?.Invoke($"# warning: {exitRanges - outEdges.Count} ExitRange rows without TerritoryType in Extra");
            foreach (var line in File.ReadLines(Path.Combine(libraryDir, "instances.csv")).Skip(1))
            {
                if (!line.Contains(",ExitRange,")) continue;
                var f = LibraryDiff.SplitCsv(line);
                if (f.Count < 17 || f[4] != "ExitRange") continue;
                if (ExtraValue(f[16], "TerritoryType") != ttStr) continue;
                var src = f[0].StartsWith("TT") && uint.TryParse(f[0][2..], out var sid) ? sid.ToString() : f[0];
                w.WriteLine($"in,{f[1]},{f[2]},{f[3]},{f[5]},{ExtraValue(f[16], "ExitType")},{src},{ExtraValue(f[16], "Index")},{f[7]},{f[8]},{f[9]}");
                inEdges.Add(src);
            }
            res.Counts["exits.csv"] = exitRanges + inEdges.Count;
        }

        // --- 9. cfc.csv (ContentFinderCondition rows for this territory) ---
        var cfcList = new List<string>();
        using (var w = Csv.OpenWriter(Out("cfc.csv")))
        {
            w.WriteLine("RowId,Name,ContentLinkType,Content,ClassJobLevelRequired,ClassJobLevelSync,HighEndDuty,TerritoryType");
            var cfc = NamedSheet.Open(env, "ContentFinderCondition", log);
            // fallback raw indices current as of 7.55 (see NOTES)
            int cTT = cfc.Col("TerritoryType", 1);
            foreach (var row in cfc.Rows)
            {
                if (Csv.Str(row.ReadColumn(cTT)) != ttStr) continue;
                var name = cfc.Str(row, "Name", 43);
                w.WriteLine(string.Join(",",
                    row.RowId, Csv.Escape(name),
                    cfc.Str(row, "ContentLinkType", 2), cfc.Str(row, "Content", 3),
                    cfc.Str(row, "ClassJobLevelRequired", 17), cfc.Str(row, "ClassJobLevelSync", 18),
                    cfc.Str(row, "HighEndDuty", 33), ttStr));
                cfcList.Add($"{row.RowId} {name}");
            }
            res.Counts["cfc.csv"] = cfcList.Count;
        }

        // --- 10. vfx.csv (VFX instances from the slice, verbatim rows) ---
        using (var w = Csv.OpenWriter(Out("vfx.csv")))
        {
            w.WriteLine(InstanceCensus.Header);
            var n = 0;
            for (var i = 0; i < instRows.Count; i++)
                if (instRows[i][4] == "VFX") { w.WriteLine(instLines[i]); n++; }
            res.Counts["vfx.csv"] = n;
        }

        // --- 11. optional collision dump ---
        if (collision)
        {
            var dir = TerritoryDump.Run(env.Game, ttStr, Path.Combine(outDir, "collision"), null, log);
            log?.Invoke($"# collision -> {dir}");
        }

        // --- 1. summary.md (written last: knows all counts) ---
        using (var w = Csv.OpenWriter(Out("summary.md")))
        {
            w.WriteLine($"# Territory workspace — TT {territoryId} ({res.Place})");
            w.WriteLine();
            w.WriteLine($"- TerritoryType: {territoryId}  Name: `{res.Name}`  PlaceName: {res.Place}");
            w.WriteLine($"- Bg: `{bg}`  level dir: `{levelDir}`  library label: {res.Label}");
            w.WriteLine($"- TerritoryIntendedUse: {intendedUse}");
            w.WriteLine();
            w.WriteLine("## Files (data rows)");
            w.WriteLine();
            foreach (var (file, count) in res.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteLine($"- `{file}`: {count}");
            if (collision) w.WriteLine("- `collision/`: Pcb TerritoryDump (collision.csv, pcb-meshes.csv, per-LGB sheets)");
            w.WriteLine();
            w.WriteLine("## ContentFinderCondition");
            w.WriteLine();
            if (cfcList.Count == 0) w.WriteLine("(none)");
            foreach (var c in cfcList) w.WriteLine($"- {c}");
            w.WriteLine();
            w.WriteLine("## Exit edges");
            w.WriteLine();
            w.WriteLine($"- out: {outEdges.Count} edge(s) -> TT {(outEdges.Count > 0 ? string.Join(", ", outEdges.Distinct()) : "-")}");
            w.WriteLine($"- in:  {inEdges.Count} edge(s) <- TT {(inEdges.Count > 0 ? string.Join(", ", inEdges.Distinct()) : "-")}");
            w.WriteLine();
            w.WriteLine("Slice files (layer-sets/layer-filters/layers/instances, quest-npcs) are exact");
            w.WriteLine("row subsets of dumps/library; derived tables are documented in");
            w.WriteLine("src/Atlas.Core/Territory/NOTES-Territory.md.");
        }

        return res;
    }

    /// <summary>Value of `key=` in a ;-separated Extra blob ("" when absent).</summary>
    static string ExtraValue(string extra, string key)
    {
        foreach (var part in extra.Split(';'))
            if (part.StartsWith(key + "=", StringComparison.Ordinal))
                return part.Substring(key.Length + 1);
        return "";
    }
}
