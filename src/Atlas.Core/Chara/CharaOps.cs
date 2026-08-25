// Equipment resolver: .imc parsing + chara variant path resolution.
//
// Chara models (chara/{equipment,accessory,weapon,monster,demihuman}/...) store
// variant-relative material refs ("/mt_c0101e0015_top_a.mtrl"). Which concrete
// material folder (".../material/v0001/") a variant uses comes from the item's
// .imc file next to the model tree. This module parses .imc and joins it with
// the model's embedded material list (Deps.DepsOps.MdlMaterials — works on v5
// and v6 models) to produce real, existence-checked mtrl paths per variant.
//
// .imc layout (reference: Lumina ImcFile, Penumbra ImcEntry):
//   u16 Count      variants per part, EXCLUDING the default entry
//   u16 PartMask   bit per part; equipment/accessory=31 (5 parts), weapons/monsters=1
//   then one 6-byte default entry per part,
//   then Count rounds of one entry per part (interleaved across parts).
// Entry: u8 MaterialId, u8 DecalId, u16 AttributeAndSound (attrs=bits 0-9,
// sound=bits 10-15), u8 VfxId, u8 MaterialAnimationId (low nibble).
//
// Part order convention (TexTools/Penumbra): equipment met,top,glv,dwn,sho;
// accessory ear,nek,wrs,rir,ril; single-part categories use part 0.
//
// eqdp (racial model existence) and est (extra skeletons) are deferred: model
// existence is answerable via the path index / Exists, and est is skeleton
// scope. Resolution here is race-agnostic — material folders are shared across
// races; the race code lives in the filename, which the mdl itself supplies.

namespace Atlas.Core.Chara;

public sealed record ImcEntry(byte MaterialId, byte DecalId, ushort AttributeMask, byte SoundId, byte VfxId, byte MaterialAnimationId);

public sealed record ImcPart(ImcEntry Default, ImcEntry[] Variants);

public sealed record ImcData(int Count, int PartMask, ImcPart[] Parts);

/// <summary>A classified chara asset path. Slot is the 3-letter suffix ("top") or "" for single-part categories.</summary>
public sealed record CharaRef(string Category, int PrimaryId, int SecondaryId, int RaceCode, string Slot);

public sealed record ResolvedMtrl(string Ref, string Path, bool Exists);

public sealed record ResolvedVariant(int Variant, ImcEntry Entry, string MaterialFolder, ResolvedMtrl[] Mtrls, string? VfxPath, bool? VfxExists);

public sealed record ResolveResult(string MdlPath, CharaRef Ref, string ImcPath, int ImcCount, int PartIndex, string[] MdlMaterials, ResolvedVariant[] Variants);

public static class CharaOps
{
    // ---- imc ----

    public static ImcData ParseImc(byte[] d)
    {
        if (d.Length < 4) throw new InvalidDataException("imc: too short");
        var count = BitConverter.ToUInt16(d, 0);
        var mask = BitConverter.ToUInt16(d, 2);
        var nParts = System.Numerics.BitOperations.PopCount((uint)(mask & 0x1F));
        if (nParts == 0) throw new InvalidDataException($"imc: no parts (mask=0x{mask:X})");
        var need = 4 + 6 * nParts * (count + 1);
        if (d.Length < need) throw new InvalidDataException($"imc: {d.Length} bytes, need {need} (count={count}, parts={nParts})");

        var off = 4;
        ImcEntry ReadEntry()
        {
            var attrSound = BitConverter.ToUInt16(d, off + 2);
            var e = new ImcEntry(d[off], d[off + 1],
                (ushort)(attrSound & 0x3FF), (byte)(attrSound >> 10),
                d[off + 4], (byte)(d[off + 5] & 0xF));
            off += 6;
            return e;
        }

        var defaults = new ImcEntry[nParts];
        for (var p = 0; p < nParts; p++) defaults[p] = ReadEntry();
        var variants = new ImcEntry[nParts][];
        for (var p = 0; p < nParts; p++) variants[p] = new ImcEntry[count];
        for (var v = 0; v < count; v++)
            for (var p = 0; p < nParts; p++)
                variants[p][v] = ReadEntry();

        var parts = new ImcPart[nParts];
        for (var p = 0; p < nParts; p++) parts[p] = new ImcPart(defaults[p], variants[p]);
        return new ImcData(count, mask, parts);
    }

    public static ImcData LoadImc(XivEnv env, string imcPath)
    {
        var f = env.Game.GetFile(imcPath) ?? throw new FileNotFoundException($"not in game data: {imcPath}");
        return ParseImc(f.Data);
    }

    // ---- path classification ----

    static readonly System.Text.RegularExpressions.Regex RxEquip = new(
        @"^chara/(?<cat>equipment|accessory)/[ea](?<pid>\d{4})/(?:model/c(?<race>\d{4})[ea]\d{4}_(?<slot>[a-z]{3})\.mdl|[ea]\d{4}\.imc)$");
    static readonly System.Text.RegularExpressions.Regex RxBody = new(
        @"^chara/(?<cat>weapon|monster)/[wm](?<pid>\d{4})/obj/body/b(?<sid>\d{4})/(?:model/[wm]\d{4}b\d{4}\.mdl|b\d{4}\.imc)$");
    static readonly System.Text.RegularExpressions.Regex RxDemi = new(
        @"^chara/demihuman/d(?<pid>\d{4})/obj/equipment/e(?<sid>\d{4})/(?:model/d\d{4}e\d{4}_(?<slot>[a-z]{3})\.mdl|e\d{4}\.imc)$");

    /// <summary>Classify a chara .mdl or .imc path. Null when not a resolvable chara asset.</summary>
    public static CharaRef? Classify(string path)
    {
        var m = RxEquip.Match(path);
        if (m.Success)
            return new CharaRef(m.Groups["cat"].Value, int.Parse(m.Groups["pid"].Value), 0,
                m.Groups["race"].Success ? int.Parse(m.Groups["race"].Value) : 0,
                m.Groups["slot"].Success ? m.Groups["slot"].Value : "");
        m = RxBody.Match(path);
        if (m.Success)
            return new CharaRef(m.Groups["cat"].Value, int.Parse(m.Groups["pid"].Value),
                int.Parse(m.Groups["sid"].Value), 0, "");
        m = RxDemi.Match(path);
        if (m.Success)
            return new CharaRef("demihuman", int.Parse(m.Groups["pid"].Value),
                int.Parse(m.Groups["sid"].Value), 0,
                m.Groups["slot"].Success ? m.Groups["slot"].Value : "");
        return null;
    }

    public static string ImcPath(CharaRef r) => r.Category switch
    {
        "equipment" => $"chara/equipment/e{r.PrimaryId:D4}/e{r.PrimaryId:D4}.imc",
        "accessory" => $"chara/accessory/a{r.PrimaryId:D4}/a{r.PrimaryId:D4}.imc",
        "weapon" => $"chara/weapon/w{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/b{r.SecondaryId:D4}.imc",
        "monster" => $"chara/monster/m{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/b{r.SecondaryId:D4}.imc",
        "demihuman" => $"chara/demihuman/d{r.PrimaryId:D4}/obj/equipment/e{r.SecondaryId:D4}/e{r.SecondaryId:D4}.imc",
        _ => throw new ArgumentException($"no imc for category {r.Category}"),
    };

    public static string MaterialFolder(CharaRef r, int variant) => r.Category switch
    {
        "equipment" => $"chara/equipment/e{r.PrimaryId:D4}/material/v{variant:D4}",
        "accessory" => $"chara/accessory/a{r.PrimaryId:D4}/material/v{variant:D4}",
        "weapon" => $"chara/weapon/w{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/material/v{variant:D4}",
        "monster" => $"chara/monster/m{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/material/v{variant:D4}",
        "demihuman" => $"chara/demihuman/d{r.PrimaryId:D4}/obj/equipment/e{r.SecondaryId:D4}/material/v{variant:D4}",
        _ => throw new ArgumentException($"no material folder for category {r.Category}"),
    };

    static string? VfxPath(CharaRef r, byte vfxId) => vfxId == 0 ? null : r.Category switch
    {
        "equipment" => $"chara/equipment/e{r.PrimaryId:D4}/vfx/eff/ve{vfxId:D4}.avfx",
        "weapon" => $"chara/weapon/w{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/vfx/eff/vw{vfxId:D4}.avfx",
        "monster" => $"chara/monster/m{r.PrimaryId:D4}/obj/body/b{r.SecondaryId:D4}/vfx/eff/vm{vfxId:D4}.avfx",
        _ => null, // accessory/demihuman: no verified template
    };

    /// <summary>Imc part index for a slot suffix; single-part files use 0.</summary>
    public static int PartIndex(CharaRef r, int partsAvailable)
    {
        if (partsAvailable <= 1) return 0;
        var i = r.Category switch
        {
            "accessory" => Array.IndexOf(AccessoryOrder, r.Slot),
            _ => Array.IndexOf(EquipOrder, r.Slot),
        };
        if (i < 0 || i >= partsAvailable)
            throw new ArgumentException($"slot '{r.Slot}' not mappable to an imc part ({r.Category}, {partsAvailable} parts)");
        return i;
    }

    static readonly string[] EquipOrder = ["met", "top", "glv", "dwn", "sho"];
    static readonly string[] AccessoryOrder = ["ear", "nek", "wrs", "rir", "ril"];

    // ---- resolve ----

    /// <summary>
    /// Resolve a chara .mdl to concrete per-variant material paths.
    /// variant: null = all (0 = default entry, 1..Count), otherwise just that one.
    /// </summary>
    public static ResolveResult Resolve(XivEnv env, string mdlPath, int? variant = null)
    {
        var r = Classify(mdlPath) ?? throw new ArgumentException($"not a resolvable chara path: {mdlPath}");
        var mdl = env.Game.GetFile(mdlPath) ?? throw new FileNotFoundException($"not in game data: {mdlPath}");
        var mats = Deps.DepsOps.MdlMaterials(mdl.Data);
        var imcPath = ImcPath(r);
        var imc = LoadImc(env, imcPath);
        var part = imc.Parts[PartIndex(r, imc.Parts.Length)];

        IEnumerable<int> wanted = variant is { } v
            ? (v < 0 || v > imc.Count ? throw new ArgumentOutOfRangeException(nameof(variant), $"variant {v} out of range 0..{imc.Count}") : [v])
            : Enumerable.Range(0, imc.Count + 1);

        var list = new List<ResolvedVariant>();
        foreach (var v2 in wanted)
        {
            var e = v2 == 0 ? part.Default : part.Variants[v2 - 1];
            var folder = MaterialFolder(r, e.MaterialId);
            var res = new ResolvedMtrl[mats.Count];
            for (var i = 0; i < mats.Count; i++)
            {
                var mref = mats[i];
                var abs = mref.StartsWith('/') ? folder + mref : mref;
                res[i] = new ResolvedMtrl(mref, abs, env.Game.FileExists(abs));
            }
            var vfx = VfxPath(r, e.VfxId);
            list.Add(new ResolvedVariant(v2, e, folder, res, vfx, vfx == null ? null : env.Game.FileExists(vfx)));
        }
        return new ResolveResult(mdlPath, r, imcPath, imc.Count, PartIndex(r, imc.Parts.Length), mats.ToArray(), list.ToArray());
    }
}
