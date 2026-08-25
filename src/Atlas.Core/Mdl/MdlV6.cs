// v6 -> v5 model shim. Dawntrail (7.0) bumped chara models to file version
// 0x01000006, which vendored Lumina's MdlFile cannot read. The structural
// deltas are small and all live in the runtime section (references:
// xivModdingFramework Mdl.cs, TexTools' v6 support):
//
//   1. Bone set block: v5 stores BoneTableCount fixed 132-byte records
//      (64x u16 indices + u32 count); v6 stores a {u16 offset, u16 count}
//      header per set followed by packed u16 indices, each set padded to
//      4-byte alignment, total block = BoneSetSize*2 + BoneSetCount*4
//      (BoneSetSize lives at model-header byte 44, a v5 unknown).
//   2. Neck morph table (7.1+, face models): u8 count at model-header byte 43,
//      32-byte entries after the submesh bone map.
//   3. Patch-7.2 table (face models): u16 count at model-header byte 48,
//      16-byte entries after the neck morph table.
//
// Everything else (headers, declarations, meshes, shapes, bounding boxes,
// vertex/index data) is byte-identical between versions. So instead of a
// parallel v6 parser, we rewrite the bone block to v5 form, excise the two
// new tables, patch the affected header fields, shift every absolute data
// offset the runtime structs hold (Lod/Mesh vertex+index buffer offsets move
// with the file growth; index-unit fields do not), and hand the result
// to stock Lumina. Bone sets with more than 64 bones are truncated (the
// viewer does not skin); the excised tables are cosmetic (neck blending).
//
// The walk is validated end-to-end: after the transform the computed end of
// the runtime section must equal VertexOffset[0]. Any mismatch throws rather
// than producing silently-wrong geometry.

namespace Atlas.Core.Mdl;

public static class MdlV6
{
    public const uint V6 = 0x0100_0006;
    public const uint V5 = 0x0100_0005;

    /// <summary>Transform a v6 .mdl image to v5. Returns the input array unchanged for any other version.</summary>
    public static byte[] ToV5(byte[] d)
    {
        if (d.Length < 0x44 || BitConverter.ToUInt32(d, 0) != V6) return d;

        static uint U32(byte[] b, long o) => BitConverter.ToUInt32(b, (int)o);
        static ushort U16(byte[] b, long o) => BitConverter.ToUInt16(b, (int)o);

        var stack = U32(d, 4);
        var declCount = U16(d, 12);
        if (stack != declCount * 136u)
            throw new InvalidDataException($"v6 shim: stack {stack} != declCount {declCount} * 136");
        long declEnd = 0x44 + stack;
        if (declEnd + 8 > d.Length) throw new InvalidDataException("v6 shim: truncated string header");
        var strSize = U32(d, declEnd + 4);
        var mh = declEnd + 8 + strSize;           // model header (56 bytes)
        if (mh + 56 > d.Length) throw new InvalidDataException("v6 shim: truncated model header");

        int meshCount = U16(d, mh + 4), attrCount = U16(d, mh + 6), submeshCount = U16(d, mh + 8);
        int materialCount = U16(d, mh + 10), boneCount = U16(d, mh + 12), boneSetCount = U16(d, mh + 14);
        int shapeCount = U16(d, mh + 16), shapeMeshCount = U16(d, mh + 18), shapeValueCount = U16(d, mh + 20);
        int elementIdCount = U16(d, mh + 24);
        int terShadowMeshCount = d[mh + 26];
        var flags2 = d[mh + 27];
        int terShadowSubmeshCount = U16(d, mh + 38);
        int neckCount = d[mh + 43];
        int boneSetSize = U16(d, mh + 44);
        int p72Count = U16(d, mh + 48);

        // No v6-specific blocks (typical of 7.x bg/furniture models): the
        // runtime section is already v5-shaped apart from the version field,
        // and stock Lumina reads it as-is (golden-proven on the composed map).
        // Pass through rather than walking bg layouts this shim does not model.
        if (boneSetSize == 0 && neckCount == 0 && p72Count == 0) return d;

        var cur = mh + 56;
        cur += elementIdCount * 32L;
        cur += 3 * 60L;                            // lods (always 3 slots)
        if ((flags2 & 0x10) != 0) cur += 3 * 40L;  // extra lods
        cur += meshCount * 36L;
        cur += attrCount * 4L;
        cur += terShadowMeshCount * 20L;
        cur += submeshCount * 16L;
        cur += terShadowSubmeshCount * 12L;
        cur += materialCount * 4L;
        cur += boneCount * 4L;

        var boneBlockOff = cur;
        long v6BoneBytes = boneSetSize * 2L + boneSetCount * 4L;
        var boneBlockEnd = boneBlockOff + v6BoneBytes;
        cur = boneBlockEnd;
        cur += shapeCount * 16L + shapeMeshCount * 12L + shapeValueCount * 4L;
        if (cur + 4 > d.Length) throw new InvalidDataException("v6 shim: walk past EOF before submesh bone map");
        var smbSize = U32(d, cur);
        cur += 4 + smbSize;
        var neckOff = cur;
        cur += neckCount * 32L;
        cur += p72Count * 16L;
        var tailOff = cur;                         // padding byte onward is kept verbatim
        if (tailOff >= d.Length) throw new InvalidDataException("v6 shim: walk past EOF at padding");
        var padAmount = d[tailOff];
        var walkEnd = tailOff + 1 + padAmount + (4L + boneCount) * 32L;
        var vtx0 = U32(d, 16);
        if (vtx0 != 0 && walkEnd != vtx0)
            throw new InvalidDataException($"v6 shim: layout mismatch (walk end 0x{walkEnd:X} != VertexOffset[0] 0x{vtx0:X})");

        // ---- v5 bone block from v6 packed sets (sequential, 4-byte aligned per set) ----
        var v5Bone = new byte[boneSetCount * 132];
        var src = boneBlockOff + boneSetCount * 4L;
        for (var i = 0; i < boneSetCount; i++)
        {
            int n = U16(d, boneBlockOff + i * 4L + 2);
            var take = Math.Min(n, 64);
            if (src + n * 2L > boneBlockEnd) throw new InvalidDataException("v6 shim: bone set overruns block");
            Buffer.BlockCopy(d, (int)src, v5Bone, i * 132, take * 2);
            BitConverter.GetBytes((uint)take).CopyTo(v5Bone, i * 132 + 128);
            src += n * 2L + (n % 2 == 1 ? 2L : 0);
        }

        var delta = boneSetCount * 132L - v6BoneBytes - neckCount * 32L - p72Count * 16L;

        // ---- assemble ----
        var outLen = d.Length + delta;
        var o = new byte[outLen];
        var w = 0;
        void Copy(long from, long len) { Buffer.BlockCopy(d, (int)from, o, w, (int)len); w += (int)len; }

        Copy(0, boneBlockOff);                     // headers, decls, strings, mh, pre-bone arrays
        Buffer.BlockCopy(v5Bone, 0, o, w, v5Bone.Length); w += v5Bone.Length;
        Copy(boneBlockEnd, neckOff - boneBlockEnd); // shapes .. submesh bone map
        Copy(tailOff, d.Length - tailOff);          // padding, bboxes, vertex + index data
        if (w != outLen) throw new InvalidDataException("v6 shim: assembly length mismatch");

        // ---- header patches on the output ----
        BitConverter.GetBytes(V5).CopyTo(o, 0);
        BitConverter.GetBytes((uint)(U32(d, 8) + delta)).CopyTo(o, 8);      // RuntimeSize
        for (var i = 0; i < 3; i++)
        {
            var vo = U32(d, 16 + i * 4);
            if (vo != 0) BitConverter.GetBytes((uint)(vo + delta)).CopyTo(o, 16 + i * 4);
            var io = U32(d, 28 + i * 4);
            if (io != 0) BitConverter.GetBytes((uint)(io + delta)).CopyTo(o, 28 + i * 4);
        }
        o[mh + 43] = 0;                                                     // neck morph count
        BitConverter.GetBytes((ushort)0).CopyTo(o, mh + 44);                // v6 bone set size
        BitConverter.GetBytes((ushort)0).CopyTo(o, mh + 48);                // patch-7.2 count

        // ---- shift absolute data offsets inside the lod structs ----
        // Each LodStruct duplicates the file-header vertex/index offsets as
        // absolute byte positions; they move with the file growth. Mesh and
        // terrain-shadow VertexBufferOffsets are lod-window-RELATIVE (stream 0
        // of the first mesh is 0) and must NOT be shifted.
        void Shift(long off)
        {
            var v = U32(o, off);
            if (v != 0) BitConverter.GetBytes((uint)(v + delta)).CopyTo(o, (int)off);
        }
        var lodOff = mh + 56 + elementIdCount * 32L;
        for (var i = 0; i < 3; i++)
        {
            Shift(lodOff + i * 60L + 32);      // EdgeGeometryDataOffset
            Shift(lodOff + i * 60L + 52);      // VertexDataOffset
            Shift(lodOff + i * 60L + 56);      // IndexDataOffset
        }

        // ---- vertex declaration fixes (buffers untouched) ----
        // v6 declares blend data in forms stock Lumina cannot decode:
        //   UInt/BlendWeights (type 5, 4B)    -> ByteFloat4 (same 4B, /255 —
        //                                        the actual weight semantics)
        //   UByte8/BlendWeights (type 17, 8B) -> Half4 (same 8B; decoded values
        //                                        are nonsense but the viewer
        //                                        does not skin)
        //   UByte8/BlendIndices (type 17, 8B) -> two UInt/BlendIndices (4B+4B;
        //                                        second write wins, harmless)
        // Reads stay byte-exact per stream, so strides and offsets still tile.
        for (var i = 0; i < declCount; i++)
        {
            var db = 0x44 + i * 136;
            var els = new List<byte[]>();
            for (var j = 0; j < 17 && o[db + j * 8] != 255; j++)
            {
                var e = new byte[8];
                Array.Copy(o, db + j * 8, e, 0, 8);
                switch (e[2], e[3])
                {
                    case (5, 1): e[2] = 8; break;
                    case (17, 1): e[2] = 14; break;
                    case (17, 2):
                        e[2] = 5;
                        var hi = (byte[])e.Clone();
                        hi[1] += 4;
                        els.Add(e); e = hi;
                        break;
                    case (17, _): throw new InvalidDataException($"v6 shim: UByte8 with unhandled usage {e[3]}");
                }
                els.Add(e);
            }
            if (els.Count > 16) throw new InvalidDataException("v6 shim: vertex declaration overflow");
            for (var k = 0; k < 17; k++)
            {
                var off = db + k * 8;
                if (k < els.Count) els[k].CopyTo(o, off);
                else if (k == els.Count) { o[off] = 255; Array.Clear(o, off + 1, 7); }
                else Array.Clear(o, off, 8);
            }
        }
        return o;
    }
}
