using System.Runtime.InteropServices;
using System.Text;

namespace ReAnimate.Formats;

// Just enough TMB (timeline) surgery for animation swaps: find the C009 "Animation (PAP
// Only)" entries and retarget their path strings. Layout (VFXEditor/XAT): TMLB header
// [magic, size, itemCount], itemCount fixed-size items, then extra + timeline + string
// pool tails. String offsets are relative to (item start + 8) and the pool is the final
// block, so a new string is appended at the end and only the one offset + TMLB size move.
public static class TmbFile
{
    private const int HeaderSize = 12;
    private const int C009PathField = 0x14;
    private const int C010PathField = 0x20;

    public static bool IsTmb(byte[] data, int offset = 0)
        => data.Length >= offset + 4 && data[offset] == 'T' && data[offset + 1] == 'M' && data[offset + 2] == 'L' && data[offset + 3] == 'B';

    // Whether the timeline carries its own sound or effect cues (C063 sound, C012 vfx,
    // C173 async vfx). Footsteps (C042) are everywhere and do not count.
    public static bool HasAudioVisual(byte[] tmb)
        => Items(tmb).Any(i => i.Magic is "C063" or "C012" or "C173");

    // Animation names the timeline references (C009/C010 paths).
    public static List<string> AnimationPaths(byte[] tmb)
    {
        var result = new List<string>();
        foreach (var (start, magic) in Items(tmb))
        {
            var field = PathField(magic);
            if (field >= 0)
                result.Add(ReadString(tmb, start, field));
        }

        return result;
    }

    // Returns a rewritten copy where every C009/C010 path found in `rename` points at its
    // new name; untouched entries keep sharing the original pool strings.
    public static byte[] RenamePaths(byte[] tmb, IReadOnlyDictionary<string, string> rename)
    {
        var output = new List<byte>(tmb);
        foreach (var (start, magic) in Items(tmb))
        {
            var field = PathField(magic);
            if (field < 0)
                continue;

            var current = ReadString(tmb, start, field);
            if (!rename.TryGetValue(current, out var replacement) || replacement == current)
                continue;

            // append the new string to the pool, point this entry's offset at it
            var stringPos = output.Count;
            output.AddRange(Encoding.ASCII.GetBytes(replacement));
            output.Add(0);
            var offset = stringPos - (start + 8);
            BitConverter.TryWriteBytes(CollectionsMarshal.AsSpan(output).Slice(start + field, 4), offset);
        }

        // TMLB size = total length
        var bytes = output.ToArray();
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
        return bytes;
    }

    // Copies the first C009's time/duration of `from` into every C009 of `to`, so a
    // target timeline (sounds, effects, facial cues) plays over a source animation of a
    // different length. Fixed-size fields, patched in place.
    public static byte[] CopyAnimationTiming(byte[] from, byte[] to)
    {
        var source = Items(from).FirstOrDefault(i => i.Magic == "C009");
        if (source.Magic != "C009")
            return to;

        var time = BitConverter.ToInt16(from, source.Start + 0x0A);
        var duration = BitConverter.ToInt32(from, source.Start + 0x0C);
        var result = (byte[])to.Clone();
        foreach (var (start, magic) in Items(result))
        {
            if (magic != "C009")
                continue;
            BitConverter.GetBytes(time).CopyTo(result, start + 0x0A);
            BitConverter.GetBytes(duration).CopyTo(result, start + 0x0C);
        }

        return result;
    }

    // Duration (frames) of the first C009, -1 without one.
    public static int C009Duration(byte[] tmb)
    {
        var item = Items(tmb).FirstOrDefault(i => i.Magic == "C009");
        return item.Magic == "C009" ? BitConverter.ToInt32(tmb, item.Start + 0x0C) : -1;
    }

    private static int PathField(string magic)
        => magic switch { "C009" => C009PathField, "C010" => C010PathField, _ => -1 };

    private static IEnumerable<(int Start, string Magic)> Items(byte[] tmb)
    {
        if (!IsTmb(tmb))
            throw new InvalidDataException("not a TMB");

        var count = BitConverter.ToInt32(tmb, 8);
        var pos = HeaderSize;
        for (var i = 0; i < count && pos + 8 <= tmb.Length; i++)
        {
            var magic = Encoding.ASCII.GetString(tmb, pos, 4);
            var size = BitConverter.ToInt32(tmb, pos + 4);
            if (size <= 0 || pos + size > tmb.Length)
                throw new InvalidDataException($"TMB item {magic} has an invalid size");
            yield return (pos, magic);
            pos += size;
        }
    }

    private static string ReadString(byte[] tmb, int itemStart, int field)
    {
        var offset = BitConverter.ToInt32(tmb, itemStart + field);
        var pos = itemStart + 8 + offset;
        var end = pos;
        while (end < tmb.Length && tmb[end] != 0)
            end++;
        return Encoding.ASCII.GetString(tmb, pos, end - pos);
    }
}
