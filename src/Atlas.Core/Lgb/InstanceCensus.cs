using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace Atlas.Core.Lgb;

/// <summary>
/// Game-wide placed-instance census (every non-BG object) — rewrite of the lost `lgbindex`
/// builder (instances.csv half), recovered verbatim from the pre-consolidation xivtool
/// Program.cs (sandbox copy). Golden: dumps/library/instances.csv.
/// Golden schema predates the identity rule (Label/Dir instead of TerritoryId) — kept exactly.
/// </summary>
public static class InstanceCensus
{
    public const string Header = "Label,Dir,File,LayerId,AssetType,InstanceId,Name,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,Extra";

    /// <summary>
    /// Write instance rows for dir-list lines ("bg/.../level\tLABEL"; missing label => "").
    /// BGInstanceObject rows are skipped (BG parts live in layers.csv BgPartCount only).
    /// Row order: input line order, then LayerFilterOps.LgbNames order, then layer order,
    /// then instance-object order. Returns (dirsWithLgbs, lgbFiles, instanceRows).
    /// </summary>
    public static (int Dirs, int Files, int Instances) Write(
        XivEnv env, IEnumerable<string> dirLines, TextWriter w, bool writeHeader = true, Action<string>? log = null)
    {
        if (writeHeader) w.WriteLine(Header);
        int nd = 0, nf = 0, ni = 0;
        foreach (var line in dirLines)
        {
            var parts = line.Split('\t');
            var dir = parts[0].TrimEnd('/');
            var lbl = parts.Length > 1 ? parts[1] : "";
            var found = false;
            foreach (var f in LayerFilterOps.LgbNames)
            {
                LgbFile? lgb = null;
                try { lgb = env.Game.GetFile<LgbFile>($"{dir}/{f}.lgb"); } catch { continue; }
                if (lgb == null) continue;
                found = true; nf++;
                foreach (var layer in lgb.Layers)
                {
                    foreach (var io in layer.InstanceObjects)
                    {
                        if (io.Object is LayerCommon.BGInstanceObject) continue;
                        var t = io.Transform;
                        var extra = io.Object switch
                        {
                            LayerCommon.ENPCInstanceObject e2 => $"BaseId={e2.ParentData.ParentData.BaseId}",
                            LayerCommon.PopRangeInstanceObject p2 => $"PopType={p2.PopType};Index={p2.Index}",
                            LayerCommon.ExitRangeInstanceObject x2 => $"ExitType={x2.ExitType};TerritoryType={x2.TerritoryType};Index={x2.Index}",
                            LayerCommon.MapRangeInstanceObject m2 => $"Map={m2.Map};PlaceNameBlock={m2.PlaceNameBlock};PlaceNameSpot={m2.PlaceNameSpot}",
                            LayerCommon.EventInstanceObject ev => $"BaseId={ev.ParentData.BaseId}",
                            LayerCommon.AetheryteInstanceObject ae => $"BaseId={ae.ParentData.BaseId}",
                            LayerCommon.SharedGroupInstanceObject sg => $"Asset={sg.AssetPath};Door={sg.InitialDoorState};Rot={sg.InitialRotationState}",
                            _ => ""
                        };
                        w.WriteLine(string.Join(",",
                            Csv.Escape(lbl), dir, f, layer.LayerId,
                            io.AssetType, io.InstanceId, Csv.Escape(io.Name ?? ""),
                            Csv.Str(t.Translation.X), Csv.Str(t.Translation.Y), Csv.Str(t.Translation.Z),
                            Csv.Str(t.Rotation.X), Csv.Str(t.Rotation.Y), Csv.Str(t.Rotation.Z),
                            Csv.Str(t.Scale.X), Csv.Str(t.Scale.Y), Csv.Str(t.Scale.Z),
                            Csv.Escape(extra)));
                        ni++;
                    }
                }
            }
            if (found) nd++;
            if (nd % 50 == 0 && found) log?.Invoke($"# {nd} dirs done...");
        }
        return (nd, nf, ni);
    }
}
