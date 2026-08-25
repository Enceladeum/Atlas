// GltfWriter - minimal, dependency-free glTF 2.0 writer (System.Text.Json + one
// external .bin buffer). New module (no xivtool ancestor): acceptance =
// reference-implementation per CONTRACT.md (clean three.js/Blender import).
//
// Scope (v1): asset/scene/nodes (TRS; rotation as quaternion XYZW), meshes with
// POSITION+NORMAL+TEXCOORD_0 float accessors + uint32 SCALAR indices, materials
// (pbrMetallicRoughness baseColorFactor; optional baseColorTexture image URI so
// textures can be wired later without changing the writer), single external
// buffer with 4-byte-aligned bufferViews, POSITION min/max (spec requirement).
// Node extras carry arbitrary JSON - the identity quadruple etc. Mesh instancing
// is the caller's job: one mesh, many nodes referencing it.
//
// Orientation: glTF is right-handed Y-up. We write game/Lumina coordinates
// UNMODIFIED - exactly like the Pcb OBJ path (TerritoryDump emits verts with no
// axis negation) - so a composed map overlays collision-mesh.obj 1:1.
// Rotation convention: game euler radians applied X, then Y, then Z in the
// row-vector sense (local = S*Rx*Ry*Rz*T, see PcbParser.LocalMatrix);
// FromEulerXyz builds the quaternion from that exact matrix product, and glTF's
// column-vector T*R*S composition reproduces the same spatial transform.

using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Atlas.Core.Gltf;

public sealed class GltfWriter
{
    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    const int CompFloat = 5126, CompUint32 = 5125;

    readonly MemoryStream _bin = new();
    readonly List<Dictionary<string, object?>> _bufferViews = new();
    readonly List<Dictionary<string, object?>> _accessors = new();
    readonly List<Dictionary<string, object?>> _meshes = new();
    readonly List<Dictionary<string, object?>> _materials = new();
    readonly List<Dictionary<string, object?>> _nodes = new();
    readonly List<Dictionary<string, object?>> _images = new();
    readonly List<Dictionary<string, object?>> _textures = new();
    readonly List<int> _sceneRoots = new();
    readonly Dictionary<string, int> _texByUri = new();   // dedupe image+texture pairs per URI

    public int NodeCount => _nodes.Count;
    public int MeshCount => _meshes.Count;

    /// <summary>Game euler (radians, applied X then Y then Z; PcbParser.LocalMatrix
    /// convention) -> quaternion. Numerically identical to the Pcb world matrices.</summary>
    public static Quaternion FromEulerXyz(Vector3 r)
    {
        var m = Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>Deterministic pastel from a path string (FNV-1a 32 -> hue,
    /// HSV(h, .45, .85)) - the shared untextured/fallback material color, stable
    /// across runs and modules (string.GetHashCode is not).</summary>
    public static Vector4 Pastel(string path)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(path)) { h ^= b; h *= 16777619u; }
        float hue = h % 360u;
        const float s = 0.45f, v = 0.85f;
        var c = v * s;
        var x = c * (1f - MathF.Abs(hue / 60f % 2f - 1f));
        var m = v - c;
        var (r, g, b2) = ((int)(hue / 60f)) switch
        {
            0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x),
            3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x),
        };
        return new Vector4(r + m, g + m, b2 + m, 1f);
    }

    static float F(float v) => float.IsFinite(v) ? v : 0f;

    // ---------- materials ----------
    /// <summary>pbrMetallicRoughness material (metallic 0, rough 1). If
    /// <paramref name="baseColorTextureUri"/> is given, an image+texture pair is
    /// emitted and wired as baseColorTexture (relative URI, resolved by the viewer).</summary>
    public int AddMaterial(string? name, Vector4 baseColor, string? baseColorTextureUri = null, bool doubleSided = true, bool alphaMask = false)
    {
        var pbr = new Dictionary<string, object?>
        {
            ["baseColorFactor"] = new[] { F(baseColor.X), F(baseColor.Y), F(baseColor.Z), F(baseColor.W) },
            ["metallicFactor"] = 0f,
            ["roughnessFactor"] = 1f,
        };
        if (baseColorTextureUri != null)
        {
            if (!_texByUri.TryGetValue(baseColorTextureUri, out var texIdx))
            {
                _images.Add(new() { ["uri"] = baseColorTextureUri });
                _textures.Add(new() { ["source"] = _images.Count - 1 });
                _texByUri[baseColorTextureUri] = texIdx = _textures.Count - 1;
            }
            pbr["baseColorTexture"] = new Dictionary<string, object?> { ["index"] = texIdx };
        }
        var m = new Dictionary<string, object?> { ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = doubleSided };
        if (alphaMask) { m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.5f; }
        if (name != null) m["name"] = name;
        _materials.Add(m);
        return _materials.Count - 1;
    }

    // ---------- meshes ----------
    /// <summary>New empty mesh; fill with AddPrimitive. A mesh with zero
    /// primitives is invalid glTF - callers must add at least one.</summary>
    public int AddMesh(string? name)
    {
        var m = new Dictionary<string, object?> { ["primitives"] = new List<object>() };
        if (name != null) m["name"] = name;
        _meshes.Add(m);
        return _meshes.Count - 1;
    }

    /// <summary>Triangle primitive. positions = xyz triples (required); normals xyz,
    /// texcoords uv - passed through only when their length matches the vertex count.</summary>
    public void AddPrimitive(int mesh, float[] positions, float[]? normals, float[]? texcoords, uint[] indices, int? material)
    {
        var vcount = positions.Length / 3;
        var attrs = new Dictionary<string, object?> { ["POSITION"] = AddFloatAccessor(positions, 3, "VEC3", minMax: true) };
        if (normals != null && normals.Length == vcount * 3) attrs["NORMAL"] = AddFloatAccessor(normals, 3, "VEC3", minMax: false);
        if (texcoords != null && texcoords.Length == vcount * 2) attrs["TEXCOORD_0"] = AddFloatAccessor(texcoords, 2, "VEC2", minMax: false);
        var prim = new Dictionary<string, object?> { ["attributes"] = attrs, ["indices"] = AddIndexAccessor(indices) };
        if (material != null) prim["material"] = material;
        ((List<object>)_meshes[mesh]["primitives"]!).Add(prim);
    }

    // ---------- nodes / scene ----------
    /// <summary>Node with optional TRS (defaults omitted), mesh and extras.
    /// glTF composes T*R*S column-vector = scale, then rotate, then translate -
    /// the same order as the game's row-vector S*Rx*Ry*Rz*T.</summary>
    public int AddNode(string? name, Vector3? translation = null, Quaternion? rotation = null, Vector3? scale = null,
        int? mesh = null, Dictionary<string, object?>? extras = null)
    {
        var n = new Dictionary<string, object?>();
        if (name != null) n["name"] = name;
        if (translation is { } t && t != Vector3.Zero) n["translation"] = new[] { F(t.X), F(t.Y), F(t.Z) };
        if (rotation is { } q && q != Quaternion.Identity) n["rotation"] = new[] { F(q.X), F(q.Y), F(q.Z), F(q.W) };
        if (scale is { } s && s != Vector3.One) n["scale"] = new[] { F(s.X), F(s.Y), F(s.Z) };
        if (mesh != null) n["mesh"] = mesh;
        if (extras is { Count: > 0 }) n["extras"] = extras;
        _nodes.Add(n);
        return _nodes.Count - 1;
    }

    public void AddChild(int parent, int child)
    {
        if (!_nodes[parent].TryGetValue("children", out var c) || c is not List<int> list)
            _nodes[parent]["children"] = list = new List<int>();
        list.Add(child);
    }

    public void AddSceneRoot(int node) => _sceneRoots.Add(node);

    // ---------- buffer plumbing ----------
    int AddBufferView(byte[] bytes, int target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);   // 4-byte alignment
        var view = new Dictionary<string, object?>
        {
            ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = bytes.Length, ["target"] = target,
        };
        _bin.Write(bytes, 0, bytes.Length);
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    int AddFloatAccessor(float[] data, int comps, string type, bool minMax)
    {
        for (var i = 0; i < data.Length; i++) if (!float.IsFinite(data[i])) data[i] = 0f;
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var acc = new Dictionary<string, object?>
        {
            ["bufferView"] = AddBufferView(bytes, ArrayBuffer),
            ["componentType"] = CompFloat, ["count"] = data.Length / comps, ["type"] = type,
        };
        if (minMax && data.Length >= comps)
        {
            var mn = new float[comps]; var mx = new float[comps];
            Array.Fill(mn, float.MaxValue); Array.Fill(mx, float.MinValue);
            for (var i = 0; i < data.Length; i++)
            {
                var c = i % comps;
                if (data[i] < mn[c]) mn[c] = data[i];
                if (data[i] > mx[c]) mx[c] = data[i];
            }
            acc["min"] = mn; acc["max"] = mx;
        }
        _accessors.Add(acc);
        return _accessors.Count - 1;
    }

    int AddIndexAccessor(uint[] indices)
    {
        var bytes = new byte[indices.Length * 4];
        Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
        _accessors.Add(new Dictionary<string, object?>
        {
            ["bufferView"] = AddBufferView(bytes, ElementArrayBuffer),
            ["componentType"] = CompUint32, ["count"] = indices.Length, ["type"] = "SCALAR",
        });
        return _accessors.Count - 1;
    }

    // ---------- output ----------
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Write .gltf JSON + external .bin. The buffer URI is the .bin file
    /// name (URI-escaped), so keep both files in the same directory.</summary>
    public void Write(string gltfPath, string binPath, string? sceneName = null)
    {
        var root = BuildRoot(Uri.EscapeDataString(Path.GetFileName(binPath)), sceneName);
        using (var bw = File.Create(binPath)) _bin.WriteTo(bw);
        using var fs = File.Create(gltfPath);
        JsonSerializer.Serialize(fs, root, JsonOpts);
    }

    /// <summary>Self-contained variant: one .gltf JSON string with the buffer
    /// embedded as a base64 data: URI (callers wanting embedded textures pass
    /// data:image/png URIs to AddMaterial). Single-response payloads; nothing
    /// touches disk. The external-file Write above stays byte-identical.</summary>
    public string WriteEmbedded(string? sceneName = null)
    {
        if (_bin.Length == 0) _bin.Write(new byte[4], 0, 4);   // pad before encoding
        var root = BuildRoot(
            "data:application/octet-stream;base64," + Convert.ToBase64String(_bin.GetBuffer(), 0, (int)_bin.Length),
            sceneName);
        return JsonSerializer.Serialize(root, JsonOpts);
    }

    Dictionary<string, object?> BuildRoot(string bufferUri, string? sceneName)
    {
        if (_bin.Length == 0) _bin.Write(new byte[4], 0, 4);   // spec: byteLength >= 1
        var scene = new Dictionary<string, object?>();
        if (sceneName != null) scene["name"] = sceneName;
        if (_sceneRoots.Count > 0) scene["nodes"] = _sceneRoots;
        var root = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?> { ["version"] = "2.0", ["generator"] = "Atlas" },
            ["scene"] = 0,
            ["scenes"] = new object[] { scene },
        };
        if (_nodes.Count > 0) root["nodes"] = _nodes;
        if (_meshes.Count > 0) root["meshes"] = _meshes;
        if (_materials.Count > 0) root["materials"] = _materials;
        if (_textures.Count > 0) { root["textures"] = _textures; root["images"] = _images; }
        if (_accessors.Count > 0) root["accessors"] = _accessors;
        if (_bufferViews.Count > 0) root["bufferViews"] = _bufferViews;
        root["buffers"] = new object[]
        {
            new Dictionary<string, object?> { ["uri"] = bufferUri, ["byteLength"] = _bin.Length },
        };
        return root;
    }
}
