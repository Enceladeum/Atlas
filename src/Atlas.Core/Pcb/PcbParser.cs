// PCB collision mesh parsing + analytic collision primitives.
// Ported verbatim from LgbDump/DumpCore.cs (golden reference) — do not redesign.
//
// PCB format (per FFXIVClientStructs BGCollision/Mesh.cs):
//   FileHeader 0x10: version i32@4 (1|4 supported), totalNodes@8, totalPrims@0xC
//   FileNode  0x30: child1 i32@8 / child2 i32@0xC (byte offsets from node start),
//     AABB f32x6 @0x10, nVertsCompressed u16@0x28, nPrims u16@0x2A, nVertsRaw u16@0x2C,
//     then f32[3*nRaw], u16[3*nCompressed] (0..65535 lerp of AABB), Primitive[nPrims]
//   Primitive 0xC: v1,v2,v3 u8 @0..2, material u64 @4
// Terrain: <levelBase>/collision/list.pcb  (header 0x20: numMeshes i32@0, AABB@4;
//   entries 0x20: meshId i32@0, AABB@4) -> tr%04d.pcb tiles, identity transform.

using System.Numerics;

namespace Atlas.Core.Pcb;

public sealed class PcbMesh
{
    public int Version;
    public int NodeCount;
    public List<Vector3> Verts = new();
    public List<(int a, int b, int c, ulong mat)> Tris = new();
    public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
}

public static class PcbParser
{
    public static PcbMesh? ParsePcb(byte[] d)
    {
        if (d.Length < 0x40) return null;
        int version = BitConverter.ToInt32(d, 4);
        if (version != 1 && version != 4) return null;
        var m = new PcbMesh { Version = version };
        var stack = new Stack<int>();
        stack.Push(0x10); // root node follows header
        while (stack.Count > 0)
        {
            int o = stack.Pop();
            if (o <= 0 || o + 0x30 > d.Length) continue;
            m.NodeCount++;
            int c1 = BitConverter.ToInt32(d, o + 8);
            int c2 = BitConverter.ToInt32(d, o + 0xC);
            var bmin = new Vector3(BitConverter.ToSingle(d, o + 0x10), BitConverter.ToSingle(d, o + 0x14), BitConverter.ToSingle(d, o + 0x18));
            var bmax = new Vector3(BitConverter.ToSingle(d, o + 0x1C), BitConverter.ToSingle(d, o + 0x20), BitConverter.ToSingle(d, o + 0x24));
            int nComp = BitConverter.ToUInt16(d, o + 0x28);
            int nPrim = BitConverter.ToUInt16(d, o + 0x2A);
            int nRaw = BitConverter.ToUInt16(d, o + 0x2C);
            int vbase = m.Verts.Count;
            int pRaw = o + 0x30;
            int pComp = pRaw + 12 * nRaw;
            int pPrim = pComp + 6 * nComp;
            if (pPrim + 12 * nPrim > d.Length) continue;
            for (int i = 0; i < nRaw; i++)
            {
                var v = new Vector3(BitConverter.ToSingle(d, pRaw + 12 * i), BitConverter.ToSingle(d, pRaw + 12 * i + 4), BitConverter.ToSingle(d, pRaw + 12 * i + 8));
                m.Verts.Add(v); m.Min = Vector3.Min(m.Min, v); m.Max = Vector3.Max(m.Max, v);
            }
            var scale = (bmax - bmin) / 65535.0f;
            for (int i = 0; i < nComp; i++)
            {
                var v = bmin + scale * new Vector3(
                    BitConverter.ToUInt16(d, pComp + 6 * i),
                    BitConverter.ToUInt16(d, pComp + 6 * i + 2),
                    BitConverter.ToUInt16(d, pComp + 6 * i + 4));
                m.Verts.Add(v); m.Min = Vector3.Min(m.Min, v); m.Max = Vector3.Max(m.Max, v);
            }
            for (int i = 0; i < nPrim; i++)
            {
                int p = pPrim + 12 * i;
                m.Tris.Add((vbase + d[p], vbase + d[p + 1], vbase + d[p + 2], BitConverter.ToUInt64(d, p + 4)));
            }
            if (c1 != 0) stack.Push(o + c1);
            if (c2 != 0) stack.Push(o + c2);
        }
        return m;
    }

    // ---------- analytic shapes (unit-size, scaled by transform; per vnavmesh conventions) ----------
    public static readonly Vector3[] BoxVerts;
    public static readonly (int, int, int)[] BoxTris;
    public static readonly Vector3[] CylVerts;
    public static readonly (int, int, int)[] CylTris;

    static PcbParser()
    {
        BoxVerts = new Vector3[8];
        for (int i = 0; i < 8; i++)
            BoxVerts[i] = new((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
        BoxTris = new (int, int, int)[]
        {
            (0,1,3),(0,3,2),(4,6,7),(4,7,5), // -z,+z? (winding not critical for inspection)
            (0,4,5),(0,5,1),(2,3,7),(2,7,6),
            (0,2,6),(0,6,4),(1,5,7),(1,7,3)
        };
        const int N = 16;
        var cv = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float a = 2 * MathF.PI * i / N;
            cv.Add(new(MathF.Cos(a), -1, MathF.Sin(a)));
            cv.Add(new(MathF.Cos(a), 1, MathF.Sin(a)));
        }
        cv.Add(new(0, -1, 0)); cv.Add(new(0, 1, 0));
        var ct = new List<(int, int, int)>();
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N;
            ct.Add((2 * i, 2 * j, 2 * i + 1)); ct.Add((2 * i + 1, 2 * j, 2 * j + 1));
            ct.Add((2 * N, 2 * j, 2 * i));       // bottom
            ct.Add((2 * N + 1, 2 * i + 1, 2 * j + 1)); // top
        }
        CylVerts = cv.ToArray(); CylTris = ct.ToArray();
    }

    /// <summary>local = S * Rx * Ry * Rz * T (System.Numerics row-vector); world = local * parentWorld.</summary>
    public static Matrix4x4 LocalMatrix(Vector3 t, Vector3 r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y)
        * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateTranslation(t);

    public static string ShapeName(uint s) => s switch
    {
        1 => "Box", 2 => "Sphere", 3 => "Cylinder", 4 => "Board", 5 => "Mesh", 6 => "BoardBothSides",
        _ => $"Shape{s}"
    };
}
