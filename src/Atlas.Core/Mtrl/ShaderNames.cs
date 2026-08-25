// Shpk resource-id name recovery. Ids in .mtrl (sampler ids, constant ids) are CRC32
// (reflected poly 0xEDB88320, init 0, NO final xor) of the EXACT-CASE ASCII name —
// verified anchors: g_SamplerNormal=0x0C5EC1F1, g_SamplerMask=0x8A4E82B6,
// g_SamplerIndex=0x565F8FD8, g_SamplerDiffuse=0x115306BE, g_SamplerColorMap0=0x1E6FEF9C.
// (Note: hashing the lowercased name does NOT reproduce the anchors; exact case does.)
// Candidate names harvested from Meddle (Meddle.Utils/Constants/Names.cs, itself sourced
// from Penumbra.GameData) plus Lumina's TextureUsage sampler enum; CRCs are computed at
// startup, so a wrong candidate simply never matches. Unknown ids stay hex.

namespace Atlas.Core.Mtrl;

public static class ShaderNames
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>FFXIV shpk name hash: reflected CRC32, poly 0xEDB88320, init 0, no final xor, exact case.</summary>
    public static uint Crc32(string name)
    {
        var c = 0u;
        foreach (var ch in name)
            c = Table[(c ^ (byte)ch) & 0xFF] ^ (c >> 8);
        return c;
    }

    public static string? Resolve(uint id) => Map.TryGetValue(id, out var n) ? n : null;

    /// <summary>Candidate g_Sampler*/g_* names; unmatched entries cost nothing.</summary>
    private static readonly string[] Candidates =
    [
        // samplers — Lumina TextureUsage enum + community-known (Meddle/Penumbra/TexTools)
        "g_Sampler", "g_Sampler0", "g_Sampler1",
        "g_SamplerAttenuation", "g_SamplerCatchlight", "g_SamplerCaustics",
        "g_SamplerColorMap", "g_SamplerColorMap0", "g_SamplerColorMap1",
        "g_SamplerDecal", "g_SamplerDiffuse", "g_SamplerDistortion", "g_SamplerDither",
        "g_SamplerEnvMap", "g_SamplerFlow", "g_SamplerFresnel", "g_SamplerGBuffer",
        "g_SamplerGradationMap", "g_SamplerIndex",
        "g_SamplerLightDiffuse", "g_SamplerLightSpecular",
        "g_SamplerMask", "g_SamplerNormal", "g_SamplerNormal2",
        "g_SamplerNormalMap", "g_SamplerNormalMap0", "g_SamplerNormalMap1",
        "g_SamplerOcclusion", "g_SamplerOmniShadowIndexTable",
        "g_SamplerReflection", "g_SamplerReflectionArray", "g_SamplerRefractionMap",
        "g_SamplerSkin", "g_SamplerSpecular",
        "g_SamplerSpecularMap", "g_SamplerSpecularMap0", "g_SamplerSpecularMap1",
        "g_SamplerSphereMap", "g_SamplerTable",
        "g_SamplerTileDiffuse", "g_SamplerTileNormal", "g_SamplerTileOrb",
        "g_SamplerWaveMap", "g_SamplerWaveMap1",
        "g_SamplerWaveletMap0", "g_SamplerWaveletMap1",
        "g_SamplerWhitecapMap", "g_SamplerWrinklesWeight",
        // constants — harvested from Meddle Names.cs (g_* MaterialParam names)
        "g_AlphaAperture", "g_AlphaMultiParam", "g_AlphaMultiWeight", "g_AlphaOffset",
        "g_AlphaThreshold", "g_AmbientOcclusionMask", "g_AngleClip", "g_CausticsPower",
        "g_CausticsReflectionPowerBright", "g_CausticsReflectionPowerDark", "g_Color",
        "g_ColorUVScale", "g_DetailColor", "g_DetailColorFadeDistance", "g_DetailColorMipBias",
        "g_DetailColorUvScale", "g_DetailID", "g_DetailNormalFadeDistance", "g_DetailNormalMipBias",
        "g_DetailNormalScale", "g_DetailNormalUvScale", "g_DiffuseColor", "g_EmissiveColor",
        "g_EnableLightShadow", "g_EnableShadow", "g_EnvMapPower", "g_FadeDistance", "g_Fresnel",
        "g_GlassIOR", "g_GlassThicknessMax", "g_Gradation", "g_HeightMapScale", "g_HeightMapUVScale",
        "g_HeightScale", "g_InclusionAperture", "g_Intensity",
        "g_IrisOptionColorEmissiveIntensity", "g_IrisOptionColorEmissiveRate", "g_IrisOptionColorRate",
        "g_IrisRingColor", "g_IrisRingEmissiveIntensity", "g_IrisRingForceColor", "g_IrisRingOddRate",
        "g_IrisRingUvFadeWidth", "g_IrisRingUvRadius", "g_IrisThickness", "g_IrisUvRadius",
        "g_LayerColor", "g_LayerDepth", "g_LayerIrregularity", "g_LayerScale", "g_LayerSoftEdge",
        "g_LayerVelocity", "g_LipRoughnessScale", "g_MultiDetailColor", "g_MultiDetailID",
        "g_MultiDetailNormalScale", "g_MultiDiffuseColor", "g_MultiEmissiveColor", "g_MultiHeightScale",
        "g_MultiNormalScale", "g_MultiSSAOMask", "g_MultiSpecularColor", "g_MultiWaveScale",
        "g_MultiWhitecapDistortion", "g_MultiWhitecapScale", "g_NearClip", "g_NormalScale",
        "g_NormalScale1", "g_NormalUVScale", "g_OutlineColor", "g_OutlineWidth", "g_PrefersFailure",
        "g_RLRReflectionPower", "g_Ray", "g_ReflectionPower", "g_RefractionColor", "g_SSAOMask",
        "g_ScatteringLevel", "g_SeaWaveScale", "g_ShaderID", "g_ShadowAlphaThreshold",
        "g_ShadowPosOffset", "g_SheenAperture", "g_SheenRate", "g_SheenTintRate", "g_Shininess",
        "g_SingleWaveScale", "g_SingleWhitecapDistortion", "g_SingleWhitecapScale",
        "g_SoftEadgDistance", "g_SpecularColor", "g_SpecularColorMask", "g_SpecularMask",
        "g_SpecularPower", "g_SpecularUVScale", "g_SphereMapID", "g_SphereMapIndex", "g_TexAnim",
        "g_TexU", "g_TexV", "g_TextureMipBias", "g_TileAlpha", "g_TileIndex", "g_TileMipBiasOffset",
        "g_TileScale", "g_ToonIndex", "g_ToonLightScale", "g_ToonLightSpecAperture",
        "g_ToonReflectionScale", "g_ToonSpecIndex", "g_Transparency", "g_TransparencyDistance",
        "g_TripleWhitecapDistortion", "g_TripleWhitecapScale", "g_UVScrollTime",
        "g_VertexMovementMaxLength", "g_VertexMovementScale", "g_WaterDeepColor",
        "g_WaveParam_NormalScale", "g_WaveParam_ScatterAlpha", "g_WaveParam_WhitecapAlpha",
        "g_WaveSpeed", "g_WaveTime", "g_WaveTime1", "g_WaveletDistortion", "g_WaveletFadeDistance",
        "g_WaveletNoiseParam", "g_WaveletOffset", "g_WaveletScale", "g_WaveletSinParam",
        "g_WhiteEyeColor", "g_WhitecapColor", "g_WhitecapDistance", "g_WhitecapDistortion",
        "g_WhitecapNoiseScale", "g_WhitecapScale", "g_WhitecapSpeed",
    ];

    private static readonly Lazy<Dictionary<uint, string>> MapLazy = new(() =>
    {
        var d = new Dictionary<uint, string>(Candidates.Length);
        foreach (var n in Candidates) d[Crc32(n)] = n;
        return d;
    });

    private static Dictionary<uint, string> Map => MapLazy.Value;
}
