namespace ReAnimate.Formats;

// Minimal .pap container codec. A pap is: header, anim-info entries (40 bytes each), a raw
// havok binary-tagfile blob, then the tmb timeline section. Only the havok blob is ever
// replaced; info and timeline bytes are preserved verbatim and the three header offsets are
// recomputed on write.
public sealed class PapFile
{
    private const uint Magic = 0x20706170; // "pap "
    private const int HeaderSize = 26;

    public int Version;
    public short AnimCount;
    public int SkeletonId;
    public byte[] InfoBytes = [];     // AnimCount * 40, kept raw
    public byte[] HavokData = [];     // binary tagfile (TAG0)
    public byte[] TimelineBytes = []; // tmb section through end of file, kept raw

    public static PapFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("not a pap file");

        var pap = new PapFile { Version = r.ReadInt32(), AnimCount = r.ReadInt16(), SkeletonId = r.ReadInt32() };
        var infoOffset = r.ReadInt32();
        var havokOffset = r.ReadInt32();
        var timelineOffset = r.ReadInt32();

        r.BaseStream.Seek(infoOffset, SeekOrigin.Begin);
        pap.InfoBytes = r.ReadBytes(havokOffset - infoOffset);
        pap.HavokData = r.ReadBytes(timelineOffset - havokOffset);
        pap.TimelineBytes = r.ReadBytes((int)(r.BaseStream.Length - timelineOffset));
        return pap;
    }

    // 40-byte info entries: char[32] name, i16 type, i16 havok index, i32 face flag.
    public const int InfoEntrySize = 40;

    public string AnimName(int i)
    {
        var span = InfoBytes.AsSpan(i * InfoEntrySize, 32);
        var len = span.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(span[..(len < 0 ? 32 : len)]);
    }

    public short AnimType(int i) => BitConverter.ToInt16(InfoBytes, i * InfoEntrySize + 32);

    public void SetAnimName(int i, string name)
    {
        var span = InfoBytes.AsSpan(i * InfoEntrySize, 32);
        span.Clear();
        System.Text.Encoding.ASCII.GetBytes(name.Length > 31 ? name[..31] : name).CopyTo(span);
    }

    // The timeline section is one TMB per animation, in info order, each self-sized by its
    // TMLB size field, 4-byte padding between them (modded exports sometimes pad oddly, so
    // the next TMB is located by its magic rather than assumed).
    public List<byte[]> Tmbs()
    {
        var result = new List<byte[]>();
        var pos = 0;
        while (pos + 12 <= TimelineBytes.Length)
        {
            if (!TmbFile.IsTmb(TimelineBytes, pos))
            {
                pos++;
                continue;
            }

            var size = BitConverter.ToInt32(TimelineBytes, pos + 4);
            if (size <= 0 || pos + size > TimelineBytes.Length)
                break;
            result.Add(TimelineBytes[pos..(pos + size)]);
            pos += size;
        }

        return result;
    }

    // Padding between TMBs is relative to the ABSOLUTE file position and keeps whatever
    // residue the havok blob ended on (vanilla: 0; some modded exports: odd) - VFXEditor's
    // rule, so the game walks them the same way it walks the original.
    public void SetTmbs(IReadOnlyList<byte[]> tmbs)
    {
        var start = HeaderSize + InfoBytes.Length + HavokData.Length;
        var residue = start % 4;
        using var ms = new MemoryStream();
        for (var i = 0; i < tmbs.Count; i++)
        {
            ms.Write(tmbs[i]);
            if (i < tmbs.Count - 1)
            {
                while ((start + ms.Length) % 4 != residue)
                    ms.WriteByte(0);
            }
        }

        TimelineBytes = ms.ToArray();
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(AnimCount);
        w.Write(SkeletonId);

        var infoOffset = HeaderSize;
        var havokOffset = infoOffset + InfoBytes.Length;
        var timelineOffset = havokOffset + HavokData.Length;
        w.Write(infoOffset);
        w.Write(havokOffset);
        w.Write(timelineOffset);
        w.Write(InfoBytes);
        w.Write(HavokData);
        w.Write(TimelineBytes);
        return ms.ToArray();
    }
}
