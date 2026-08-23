namespace ReAnimate.Formats;

// Pure byte-level retarget of a pap at another animation's names; dependency-free so the
// offline checks can run it against real game files.
public static class PapRetarget
{
    // Source pap with its animation names replaced by the target pap's, matched by entry
    // type (an idle.pap is [additive flinch, base idle]; index order would misname both).
    public static byte[] Retarget(byte[] sourceBytes, byte[] targetBytes, out Dictionary<string, string> renamed)
    {
        var source = PapFile.Read(sourceBytes);
        var target = PapFile.Read(targetBytes);
        renamed = new Dictionary<string, string>(StringComparer.Ordinal);

        // pass 1: same-type matches (base <-> base, additive <-> additive); pass 2: whatever
        // is left, in order. Sources with no partner keep their own name.
        var used = new bool[target.AnimCount];
        var pick = new int[source.AnimCount];
        Array.Fill(pick, -1);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < source.AnimCount; i++)
            {
                if (pick[i] >= 0)
                    continue;
                for (var j = 0; j < target.AnimCount; j++)
                {
                    if (used[j] || (pass == 0 && target.AnimType(j) != source.AnimType(i)))
                        continue;
                    pick[i] = j;
                    used[j] = true;
                    break;
                }
            }
        }

        for (var i = 0; i < source.AnimCount; i++)
        {
            if (pick[i] < 0)
                continue;
            var oldName = source.AnimName(i);
            var newName = target.AnimName(pick[i]);
            if (oldName != newName)
            {
                renamed[oldName] = newName;
                source.SetAnimName(i, newName);
            }
        }

        // sound is never lost: a per-animation timeline with its own cues is kept (renamed),
        // otherwise the target's timeline plays under the source with the source's C009 timing
        var tmbs = source.Tmbs();
        var targetTmbs = target.Tmbs();
        for (var i = 0; i < tmbs.Count; i++)
        {
            var useTarget = !TmbFile.HasAudioVisual(tmbs[i]) && pick.Length > i && pick[i] >= 0 && pick[i] < targetTmbs.Count;
            if (useTarget)
                tmbs[i] = TmbFile.CopyAnimationTiming(tmbs[i], targetTmbs[pick[i]]);
            else if (renamed.Count > 0)
                tmbs[i] = TmbFile.RenamePaths(tmbs[i], renamed);
        }

        source.SetTmbs(tmbs);
        return source.Write();
    }
}
