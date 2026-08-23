namespace ReAnimate.Formats;

// Minimal .sklb reader: we only need the embedded havok blob (skeleton tagfile). Header
// version 0x3132 stores 16-bit offsets, newer versions 32-bit.
public static class SklbFile
{
    public static byte[] HavokData(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt32() != 0x736B6C62u) // "blks"
            throw new InvalidDataException("not a sklb file");

        _ = r.ReadInt16();
        var header2 = r.ReadInt16();
        int havokOffset;
        if (header2 == 0x3132)
        {
            _ = r.ReadInt16();
            havokOffset = r.ReadInt16();
        }
        else
        {
            _ = r.ReadInt32();
            havokOffset = r.ReadInt32();
        }

        return data[havokOffset..];
    }
}
