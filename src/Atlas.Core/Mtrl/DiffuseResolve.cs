// DiffuseResolve - the shared "which texture is this material's diffuse?" rule.
// Single home for the sampler-priority contract (CONTRACT.md, Acceptance):
// g_SamplerColorMap0 -> g_SamplerDiffuse -> *_d.tex -> first .tex; "dummy"
// paths excluded; mip capped so max(width,height)>>mip <= maxDim (0 = mip 0).
// Consumers: Compose (whole-map textured export) and Mdl (single-model glTF) -
// keep them on this one implementation so previews and map exports agree.
// May throw on malformed game data; callers wrap (and count the fallback).

using System.Text;
using Lumina;
using Lumina.Data.Files;

namespace Atlas.Core.Mtrl;

public static class DiffuseResolve
{
    public readonly record struct Diffuse(string TexPath, TexFile Tex, int Mip);

    /// <summary>Resolve a .mtrl's diffuse texture, or null when there is nothing
    /// usable (missing material, dummy-only textures, unloadable tex).</summary>
    public static Diffuse? TryResolve(GameData gd, string mtrlPath, int maxDim)
    {
        var mtrl = gd.GetFile<MtrlFile>(mtrlPath);
        if (mtrl == null) return null;
        var texPaths = new List<string>();
        foreach (var off in mtrl.TextureOffsets) texPaths.Add(CString(mtrl.Strings, off.Offset));
        string? ByName(string want)
        {
            foreach (var s in mtrl.Samplers)
                if (s.TextureIndex < texPaths.Count && ShaderNames.Resolve(s.SamplerId) == want)
                    return texPaths[s.TextureIndex];
            return null;
        }
        var texPath = ByName("g_SamplerColorMap0") ?? ByName("g_SamplerDiffuse")
            ?? texPaths.FirstOrDefault(p => p.EndsWith("_d.tex"))
            ?? texPaths.FirstOrDefault(p => p.EndsWith(".tex"));
        if (string.IsNullOrEmpty(texPath) || texPath.Contains("dummy")) return null;
        var tex = gd.GetFile<TexFile>(texPath);
        if (tex == null) return null;
        var mip = 0;
        if (maxDim > 0)
        {
            var mips = Math.Max(1, (int)tex.Header.MipCount);
            while (mip + 1 < mips &&
                   ((tex.Header.Width >> mip) > maxDim || (tex.Header.Height >> mip) > maxDim))
                mip++;
        }
        return new Diffuse(texPath, tex, mip);
    }

    static string CString(byte[] data, int at)
    {
        if (at < 0 || at >= data.Length) return "";
        var e = at;
        while (e < data.Length && data[e] != 0) e++;
        return Encoding.UTF8.GetString(data, at, e - at);
    }
}
