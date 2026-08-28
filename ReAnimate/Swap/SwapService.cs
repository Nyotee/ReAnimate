using ReAnimate.Bake;
using ReAnimate.Formats;

namespace ReAnimate.Swap;

// Retargets one modded animation at another emote: every key of the target, each race's
// source file on that race's path, landing in the source mod (an option, or in place).
public static class SwapService
{
    public const string SwapGroup = "ReAnimate swaps";
    private const int SwapGroupPriority = 99;

    public static void Apply(Plugin plugin, string sourceModDir, string sourceModName, ModAnim anim, AnimTarget target)
    {
        var modRoot = plugin.Penumbra.ModDirectory;
        if (modRoot is null)
        {
            Plugin.Print("Penumbra is not available.");
            return;
        }

        var bakes = new Dictionary<string, byte[]>();
        var retargeted = new Dictionary<(string File, string Key), (byte[] Bytes, Dictionary<string, string> Renamed)>();
        var sourceTmbs = ModScanner.ScanTmbs(modRoot, sourceModDir);
        var sourceKey = Path.ChangeExtension(anim.RelKey[(anim.RelKey.IndexOf('/') + 1)..], null);
        try
        {
            foreach (var variant in target.Variants)
            {
                var paths = variant.RelKeys.SelectMany(AnimationCatalog.GamePaths).Distinct().ToList();
                if (paths.Count == 0)
                    continue;
                var vanilla = Plugin.DataManager.GetFile(paths[0]);
                if (vanilla is null)
                    continue;

                // the matching half of a loop+start pair, or the loop for a plain emote key
                var part = anim.PartFor(variant.Kind);
                if (part is null)
                    continue;

                Dictionary<string, string>? renamedForTmb = null;
                foreach (var path in paths)
                {
                    var sourceFile = part.FileFor(AnimationCatalog.RaceOf(path));
                    if (!retargeted.TryGetValue((sourceFile, variant.Key), out var done))
                    {
                        var bytes = PapRetarget.Retarget(File.ReadAllBytes(sourceFile), vanilla.Data, out var renamed);
                        done = (bytes, renamed);
                        retargeted[(sourceFile, variant.Key)] = done;
                        Plugin.Log.Debug($"swap {anim.RelKey} -> {variant.Key}: {string.Join(", ", renamed.Select(kv => $"{kv.Key}->{kv.Value}"))}");
                    }

                    bakes[path] = done.Bytes;
                    renamedForTmb ??= done.Renamed;
                }

                // the mod's own action timeline for this emote (sounds/effects cues) rides
                // along, re-pointed at the target's names; references only, no sound files
                if (sourceTmbs.TryGetValue($"chara/action/{sourceKey}.tmb", out var tmbFile) && renamedForTmb is not null)
                    bakes[$"chara/action/{variant.Key}.tmb"] = TmbFile.RenamePaths(File.ReadAllBytes(tmbFile), renamedForTmb);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
        {
            Plugin.Log.Error(ex, "swap failed");
            Plugin.Print($"Could not rewrite that animation: {ex.Message}");
            return;
        }

        if (bakes.Count == 0)
        {
            Plugin.Print($"No vanilla files found for {target.Name}.");
            return;
        }

        var unified = ModWriter.IsUnifiedMeta(modRoot, sourceModDir);
        var optionName = $"{anim.Display} → {target.Name}";

        // in place: the mod's own option stops replacing the old emote and replaces the new one
        if (!plugin.Config.SwapAsOption && unified
            && ModWriter.ReplaceInPlace(modRoot, sourceModDir, anim.Group, anim.Option, anim.GamePaths.ToList(), bakes))
        {
            plugin.Penumbra.ReloadMod(sourceModDir);
            plugin.Penumbra.RedrawPlayer();
            Plugin.Print($"{sourceModName} now plays {anim.Display} as {target.Name}.");
            return;
        }

        if (unified)
        {
            ModWriter.WriteOption(modRoot, sourceModDir, sourceModName, SwapGroup, optionName, bakes, SwapGroupPriority);
            BakeService.Publish(plugin, sourceModDir, isNew: false, SwapGroup, optionName);
            Plugin.Print($"Added \"{optionName}\" to {sourceModName}. Turn it off in Penumbra whenever.");
            return;
        }

        // pre-unified mod layout: a sibling mod next to it
        var sibling = SiblingDir(sourceModName);
        var isNew = !plugin.Penumbra.KnowsMod(sibling);
        ModWriter.WriteOption(modRoot, sibling, sibling, SwapGroup, optionName, bakes, SwapGroupPriority);
        BakeService.Publish(plugin, sibling, isNew, SwapGroup, optionName);
        Plugin.Print($"Added \"{optionName}\" as a new mod, \"{sibling}\". Turn it off in Penumbra whenever.");
    }

    private static string SiblingDir(string sourceModName)
    {
        var name = $"{sourceModName} - ReAnimate swaps";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // Swaps already made from a source mod: source display -> targets, read off the
    // "ReAnimate swaps" group in the mod itself and in its sibling.
    public static Dictionary<string, List<string>> ExistingSwaps(Plugin plugin, string modRoot, string sourceModDir, string sourceModName)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dir in new[] { sourceModDir, SiblingDir(sourceModName) })
        {
            var meta = ModMeta.Parse(Path.Combine(modRoot, dir, "meta.json"));
            if (meta is null)
                continue;
            foreach (var (group, option) in ModMeta.Options(meta))
            {
                if (!string.Equals(group["Name"]?.ToString(), SwapGroup, StringComparison.Ordinal))
                    continue;
                var name = option["Name"]?.ToString() ?? "";
                var arrow = name.IndexOf(" → ", StringComparison.Ordinal);
                if (arrow < 0)
                    continue;
                var from = name[..arrow];
                if (!result.TryGetValue(from, out var list))
                    result[from] = list = [];
                list.Add(name[(arrow + 3)..]);
            }
        }

        return result;
    }
}
