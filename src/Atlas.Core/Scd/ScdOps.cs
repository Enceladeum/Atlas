// Scd module — .scd sound-container inspection and audio export. New for Atlas
// (no xivtool ancestor); acceptance = real BGM (OggVorbis) and SFX (MsAdpcm)
// entries export to files that decode/play externally.
//
// Raw parser, not Lumina's ScdFile: Lumina's OggVorbis branch reads the Ogg
// header from the data position and overruns EOF — measured on
// music/ffxiv/BGM_Ride_YTC.scd, SubInfoSize (9867) already contains the
// seek-struct (32) + seek table (5396) + Ogg header (4439), and the vorbis
// stream begins at subInfoStart + SubInfoSize. Layout (little-endian):
//   SEDBSSCF binary header: magic 8, version u32, endian u8, align u8,
//     headerOffset u16, fileSize u64, ...
//   ScdHeader @headerOffset: soundCount u16, trackCount u16, audioCount u16,
//     number u16, trackOffset u32, audioOffset u32, layout/routing/attribute u32,
//     eofPadding u16 — audio entry offsets = audioCount x u32 @audioOffset.
//   AudioBasicDesc (32B) per entry: dataSize, channels, rate, format i32,
//     loopStart, loopEnd, subInfoSize, flags. SubInfo may open with a marker
//     chunk (flag 0x01: id u32, size u32, loopStartSample i32, loopEndSample
//     i32, numMarkers i32, markers...). OggVorbis (6): seek-struct
//     (encVersion u8, structSize u8, xorByte u8, pad 9, step f32,
//     seekTableSize u32, oggHeaderSize u32, pad 8) + seek table + Ogg header,
//     then data @subInfoStart+subInfoSize. Deobfuscation per encVersion:
//     2 = header XOR with xorByte, 3 = full-stream table XOR (the well-known
//     FFXIV Explorer table, as in Lumina/VFXEditor). MsAdpcm (12): a literal
//     WAVEFORMATEX-for-ADPCM in subinfo, data @subInfoStart+subInfoSize —
//     decoded here to PCM16 so exports/browser playback need no codec support.
//     The decoder uses the standard 7 Microsoft coefficient pairs (FFXIV
//     encoders emit NumCoef==7); predictor indexes clamp to 6.
// Ogg durations come from the last page's granule position / rate.

using System.Text;

namespace Atlas.Core.Scd;

public sealed record ScdEntryInfo(
    int Index,
    string Format,
    int FormatValue,
    int Channels,
    int Rate,
    long DataBytes,
    uint LoopStart,
    uint LoopEnd,
    int Markers,
    double? Seconds,
    string? Error);

public sealed record ScdInfo(
    string Path,
    int SoundCount,
    int TrackCount,
    int AudioCount,
    List<ScdEntryInfo> Entries);

public static class ScdOps
{
    const int FmtEmpty = -1, FmtOgg = 6, FmtMp3 = 7, FmtAdpcm = 12, FmtAtrac9 = 22;

    static string FormatName(int f) => f switch
    {
        FmtEmpty => "Empty", FmtOgg => "OggVorbis", FmtMp3 => "Mp3",
        FmtAdpcm => "MsAdpcm", FmtAtrac9 => "Atrac9", _ => $"Unknown{f}",
    };

    sealed record Entry(int Offset, long DataSize, int Channels, int Rate, int Format,
        uint LoopStart, uint LoopEnd, int SubInfoSize, uint Flags, int Markers);

    static byte[] Load(XivEnv env, string path, out ushort soundCount, out ushort trackCount, out int[] audioOffsets)
    {
        var f = env.Game.GetFile(path) ?? throw new FileNotFoundException($"not found: {path}");
        var d = f.Data;
        if (d.Length < 0x30 || !"SEDBSSCF"u8.SequenceEqual(d.AsSpan(0, 8)))
            throw new InvalidDataException($"not an SEDBSSCF scd: {path}");
        if (d[12] != 0) throw new InvalidDataException("big-endian scd unsupported");
        int hdr = BitConverter.ToUInt16(d, 14);
        soundCount = BitConverter.ToUInt16(d, hdr);
        trackCount = BitConverter.ToUInt16(d, hdr + 2);
        int audioCount = BitConverter.ToUInt16(d, hdr + 4);
        int audioOffset = BitConverter.ToInt32(d, hdr + 12);
        audioOffsets = new int[audioCount];
        for (var i = 0; i < audioCount; i++)
            audioOffsets[i] = BitConverter.ToInt32(d, audioOffset + i * 4);
        return d;
    }

    static Entry ReadEntry(byte[] d, int off)
    {
        var size = BitConverter.ToUInt32(d, off);
        var ch = BitConverter.ToInt32(d, off + 4);
        var rate = BitConverter.ToInt32(d, off + 8);
        var fmt = BitConverter.ToInt32(d, off + 12);
        var ls = BitConverter.ToUInt32(d, off + 16);
        var le = BitConverter.ToUInt32(d, off + 20);
        var sub = BitConverter.ToInt32(d, off + 24);
        var flg = BitConverter.ToUInt32(d, off + 28);
        var markers = 0;
        if ((flg & 1) != 0 && off + 32 + 20 <= d.Length)
            markers = BitConverter.ToInt32(d, off + 32 + 16);
        return new Entry(off, size, ch, rate, fmt, ls, le, sub, flg, markers);
    }

    /// <summary>SubInfo position after an optional leading marker chunk.</summary>
    static int PostMarkerPos(byte[] d, Entry e)
    {
        var p = e.Offset + 32;
        if ((e.Flags & 1) != 0)
            p += BitConverter.ToInt32(d, p + 4); // chunk header: id u32, size u32
        return p;
    }

    // ---------- info ----------

    public static ScdInfo Info(XivEnv env, string path)
    {
        var d = Load(env, path, out var sc, out var tc, out var offs);
        var entries = new List<ScdEntryInfo>();
        for (var i = 0; i < offs.Length; i++)
        {
            try
            {
                var e = ReadEntry(d, offs[i]);
                double? seconds = null;
                if (e.Format == FmtAdpcm)
                {
                    var w = PostMarkerPos(d, e);
                    int blockAlign = BitConverter.ToInt16(d, w + 12);
                    int samplesPerBlock = BitConverter.ToUInt16(d, w + 18);
                    if (blockAlign > 0 && e.Rate > 0)
                        seconds = e.DataSize / blockAlign * (double)samplesPerBlock / e.Rate;
                }
                else if (e.Format == FmtOgg && e.Rate > 0)
                {
                    var granule = LastOggGranule(d, e);
                    if (granule > 0) seconds = granule / (double)e.Rate;
                }
                entries.Add(new ScdEntryInfo(i, FormatName(e.Format), e.Format, e.Channels, e.Rate,
                    e.DataSize, e.LoopStart, e.LoopEnd, e.Markers, seconds, null));
            }
            catch (Exception ex)
            {
                entries.Add(new ScdEntryInfo(i, "Error", 0, 0, 0, 0, 0, 0, 0, null, ex.Message));
            }
        }
        return new ScdInfo(path, sc, tc, offs.Length, entries);
    }

    static long LastOggGranule(byte[] d, Entry e)
    {
        // Last "OggS" page's granule position (u64 at +6), from a decrypted
        // copy of the stream tail. encVersion 3 XORs the whole stream
        // positionally over header+data, so tail byte k of the data maps to
        // stream index oggHeaderSize + k; encVersion 0/2 leave the data clear.
        var p = PostMarkerPos(d, e);
        var encVersion = d[p];
        var oggHeaderSize = BitConverter.ToInt32(d, p + 20);
        var dataStart = e.Offset + 32 + e.SubInfoSize;
        var len = (int)Math.Min(e.DataSize, d.Length - dataStart);
        if (len < 27) return -1;
        var tailLen = Math.Min(len, 65536);
        var tailOff = len - tailLen; // offset into the data portion
        var tail = new byte[tailLen];
        Array.Copy(d, dataStart + tailOff, tail, 0, tailLen);
        if (encVersion == 3)
        {
            var b1 = (byte)(e.DataSize & 0x7F);
            var b2 = (byte)(b1 & 0x3F);
            for (var k = 0; k < tailLen; k++)
            {
                var j = oggHeaderSize + tailOff + k; // stream index
                tail[k] = (byte)(OggXorTable[(b2 + j) & 0xFF] ^ tail[k] ^ b1);
            }
        }
        for (var q = tailLen - 27; q >= 0; q--)
            if (tail[q] == 'O' && tail[q + 1] == 'g' && tail[q + 2] == 'g' && tail[q + 3] == 'S')
                return BitConverter.ToInt64(tail, q + 6);
        return -1;
    }

    // ---------- export ----------

    public static (byte[] Data, string Ext, string Mime) Export(XivEnv env, string path, int entry)
    {
        var d = Load(env, path, out _, out _, out var offs);
        if (entry < 0 || entry >= offs.Length)
            throw new ArgumentOutOfRangeException(nameof(entry), $"entry {entry} out of range (0..{offs.Length - 1})");
        var e = ReadEntry(d, offs[entry]);
        return e.Format switch
        {
            FmtOgg => (ExtractOgg(d, e), "ogg", "audio/ogg"),
            FmtAdpcm => (ExtractAdpcmWav(d, e), "wav", "audio/wav"),
            FmtEmpty => throw new InvalidDataException($"entry {entry} is empty"),
            _ => (Slice(d, e.Offset + 32 + e.SubInfoSize, (int)e.DataSize), "bin", "application/octet-stream"),
        };
    }

    static byte[] Slice(byte[] d, int start, int len)
    {
        if (start < 0 || start + len > d.Length) throw new InvalidDataException("audio data out of bounds");
        var r = new byte[len];
        Array.Copy(d, start, r, 0, len);
        return r;
    }

    static byte[] ExtractOgg(byte[] d, Entry e)
    {
        var p = PostMarkerPos(d, e);
        var encVersion = d[p];
        var xorByte = d[p + 2];
        var seekTableSize = BitConverter.ToInt32(d, p + 16);
        var oggHeaderSize = BitConverter.ToInt32(d, p + 20);
        var headerStart = p + 32 + seekTableSize;
        var dataStart = e.Offset + 32 + e.SubInfoSize;

        var ogg = new byte[oggHeaderSize + e.DataSize];
        Array.Copy(d, headerStart, ogg, 0, oggHeaderSize);
        Array.Copy(d, dataStart, ogg, oggHeaderSize, e.DataSize);

        switch (encVersion)
        {
            case 2:
                for (var j = 0; j < oggHeaderSize; j++) ogg[j] ^= xorByte;
                break;
            case 3:
                var b1 = (byte)(e.DataSize & 0x7F);
                var b2 = (byte)(b1 & 0x3F);
                for (var j = 0; j < ogg.Length; j++)
                    ogg[j] = (byte)(OggXorTable[(b2 + j) & 0xFF] ^ ogg[j] ^ b1);
                break;
        }
        if (ogg.Length < 4 || ogg[0] != 'O' || ogg[1] != 'g' || ogg[2] != 'g' || ogg[3] != 'S')
            throw new InvalidDataException($"decoded stream is not Ogg (encVersion {encVersion})");
        return ogg;
    }

    static byte[] ExtractAdpcmWav(byte[] d, Entry e)
    {
        var w = PostMarkerPos(d, e);
        int channels = Math.Max(1, (int)BitConverter.ToInt16(d, w + 2));
        int rate = BitConverter.ToInt32(d, w + 4);
        int blockAlign = BitConverter.ToInt16(d, w + 12);
        var adpcm = Slice(d, e.Offset + 32 + e.SubInfoSize, (int)e.DataSize);
        return AdpcmToPcmWav(adpcm, channels, rate, blockAlign);
    }

    // ---------- MS-ADPCM -> PCM16 WAV ----------

    static readonly int[] AdaptTable =
    {
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    };
    // Standard Microsoft coefficient pairs (predictor 0..6).
    static readonly int[] Coef1 = { 256, 512, 0, 192, 240, 460, 392 };
    static readonly int[] Coef2 = { 0, -256, 0, 64, 0, -208, -232 };

    static byte[] AdpcmToPcmWav(byte[] adpcm, int channels, int rate, int blockAlign)
    {
        if (blockAlign <= 0) throw new InvalidDataException("bad BlockAlign");
        var pcm = new List<short>(adpcm.Length * 2);

        for (var blockStart = 0; blockStart + 7 * channels <= adpcm.Length; blockStart += blockAlign)
        {
            var end = Math.Min(blockStart + blockAlign, adpcm.Length);
            var p = blockStart;
            var pred = new int[channels];
            var delta = new int[channels];
            var s1 = new int[channels];
            var s2 = new int[channels];
            for (var c = 0; c < channels; c++) pred[c] = Math.Min((int)adpcm[p++], 6);
            for (var c = 0; c < channels; c++) { delta[c] = BitConverter.ToInt16(adpcm, p); p += 2; }
            for (var c = 0; c < channels; c++) { s1[c] = BitConverter.ToInt16(adpcm, p); p += 2; }
            for (var c = 0; c < channels; c++) { s2[c] = BitConverter.ToInt16(adpcm, p); p += 2; }

            // The two seed samples per channel, oldest first, interleaved.
            for (var c = 0; c < channels; c++) pcm.Add((short)s2[c]);
            for (var c = 0; c < channels; c++) pcm.Add((short)s1[c]);

            var ch = 0;
            for (; p < end; p++)
            {
                for (var half = 0; half < 2; half++)
                {
                    var nibble = half == 0 ? adpcm[p] >> 4 : adpcm[p] & 0xF;
                    var signed = nibble >= 8 ? nibble - 16 : nibble;
                    var c = ch;
                    var predicted = (s1[c] * Coef1[pred[c]] + s2[c] * Coef2[pred[c]]) / 256 + signed * delta[c];
                    predicted = Math.Clamp(predicted, short.MinValue, short.MaxValue);
                    s2[c] = s1[c];
                    s1[c] = predicted;
                    delta[c] = Math.Max(16, AdaptTable[nibble] * delta[c] / 256);
                    pcm.Add((short)predicted);
                    ch = (ch + 1) % channels;
                }
            }
        }

        return WrapPcmWav(pcm, channels, rate);
    }

    static byte[] WrapPcmWav(List<short> pcm, int channels, int rate)
    {
        var dataBytes = pcm.Count * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    // The well-known FFXIV Ogg obfuscation table (FFXIV Explorer lineage, as in
    // Lumina and VFXEditor).
    static readonly byte[] OggXorTable =
    {
        0x3A, 0x32, 0x32, 0x32, 0x03, 0x7E, 0x12, 0xF7, 0xB2, 0xE2, 0xA2, 0x67, 0x32, 0x32, 0x22, 0x32,
        0x32, 0x52, 0x16, 0x1B, 0x3C, 0xA1, 0x54, 0x7B, 0x1B, 0x97, 0xA6, 0x93, 0x1A, 0x4B, 0xAA, 0xA6,
        0x7A, 0x7B, 0x1B, 0x97, 0xA6, 0xF7, 0x02, 0xBB, 0xAA, 0xA6, 0xBB, 0xF7, 0x2A, 0x51, 0xBE, 0x03,
        0xF4, 0x2A, 0x51, 0xBE, 0x03, 0xF4, 0x2A, 0x51, 0xBE, 0x12, 0x06, 0x56, 0x27, 0x32, 0x32, 0x36,
        0x32, 0xB2, 0x1A, 0x3B, 0xBC, 0x91, 0xD4, 0x7B, 0x58, 0xFC, 0x0B, 0x55, 0x2A, 0x15, 0xBC, 0x40,
        0x92, 0x0B, 0x5B, 0x7C, 0x0A, 0x95, 0x12, 0x35, 0xB8, 0x63, 0xD2, 0x0B, 0x3B, 0xF0, 0xC7, 0x14,
        0x51, 0x5C, 0x94, 0x86, 0x94, 0x59, 0x5C, 0xFC, 0x1B, 0x17, 0x3A, 0x3F, 0x6B, 0x37, 0x32, 0x32,
        0x30, 0x32, 0x72, 0x7A, 0x13, 0xB7, 0x26, 0x60, 0x7A, 0x13, 0xB7, 0x26, 0x50, 0xBA, 0x13, 0xB4,
        0x2A, 0x50, 0xBA, 0x13, 0xB5, 0x2E, 0x40, 0xFA, 0x13, 0x95, 0xAE, 0x40, 0x38, 0x18, 0x9A, 0x92,
        0xB0, 0x38, 0x00, 0xFA, 0x12, 0xB1, 0x7E, 0x00, 0xDB, 0x96, 0xA1, 0x7C, 0x08, 0xDB, 0x9A, 0x91,
        0xBC, 0x08, 0xD8, 0x1A, 0x86, 0xE2, 0x70, 0x39, 0x1F, 0x86, 0xE0, 0x78, 0x7E, 0x03, 0xE7, 0x64,
        0x51, 0x9C, 0x8F, 0x34, 0x6F, 0x4E, 0x41, 0xFC, 0x0B, 0xD5, 0xAE, 0x41, 0xFC, 0x0B, 0xD5, 0xAE,
        0x41, 0xFC, 0x3B, 0x70, 0x71, 0x64, 0x33, 0x32, 0x12, 0x32, 0x32, 0x36, 0x70, 0x34, 0x2B, 0x56,
        0x22, 0x70, 0x3A, 0x13, 0xB7, 0x26, 0x60, 0xBA, 0x1B, 0x94, 0xAA, 0x40, 0x38, 0x00, 0xFA, 0xB2,
        0xE2, 0xA2, 0x67, 0x32, 0x32, 0x12, 0x32, 0xB2, 0x32, 0x32, 0x32, 0x32, 0x75, 0xA3, 0x26, 0x7B,
        0x83, 0x26, 0xF9, 0x83, 0x2E, 0xFF, 0xE3, 0x16, 0x7D, 0xC0, 0x1E, 0x63, 0x21, 0x07, 0xE3, 0x01,
    };
}
