using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ReAnimate.Formats;
using ReAnimate.Game;
using ReAnimate.Havok;

namespace ReAnimate.Bake;

// One action = snap the current pose, bake loop (+_start) paps for the chosen race and
// slot, upsert them as a Penumbra mod option ("Miqo'te Female" group, "Doze 2" option),
// enable it in the live collection, redraw. Saves are managed (toggled, deleted) straight
// in Penumbra; the plugin only ever adds. Runs on the framework thread.
public static class BakeService
{
    // /reanimate save: the stance on screen; falls back to the last saved slot.
    public static void SaveCurrent(Plugin plugin)
    {
        var info = GameState.ReadPlayerInfo();
        if (info is not null && GameState.BakeableFamilies.Contains(info.Family))
            Bake(plugin, info.Family, info.Slot);
        else if (plugin.Config.LastFamily >= 0)
            Bake(plugin, (EmoteController.PoseType)plugin.Config.LastFamily, plugin.Config.LastSlot);
        else
            Bake(plugin, EmoteController.PoseType.Idle, 0);
    }

    // raceSexId null = the player's own race. slot null = the live cpose when in that
    // stance, else the base pose.
    public static void Bake(Plugin plugin, EmoteController.PoseType family, byte? slot, ushort? raceSexId = null)
    {
        var modRoot = plugin.Penumbra.ModDirectory;
        if (modRoot is null)
        {
            Plugin.Print("Penumbra is not available - it is required to save.");
            return;
        }

        var info = GameState.ReadPlayerInfo();
        if (info is null)
        {
            Plugin.Print("Log in with a (human) character first.");
            return;
        }

        var race = raceSexId ?? info.RaceSexId;
        var targetSlot = slot ?? GameState.LiveSlot(family) ?? 0;
        if (targetSlot >= GameState.SlotCount(family))
        {
            Plugin.Print($"{GameState.FamilyName(family)} has no pose {targetSlot}.");
            return;
        }

        var battleFolder = GameState.BattleFolder(info.ClassJob);
        var target = IdleResolver.Resolve(family, targetSlot, race, info.AnimVariant, battleFolder, out var error);
        if (target is null)
        {
            Plugin.Print($"Cannot save: {error}.");
            return;
        }

        var actor = GameState.ResolvePoseActor();
        var player = Plugin.Objects.LocalPlayer;
        var baseline = actor is not null && player is not null && actor.Address != player.Address ? player : null;
        var pose = actor is null ? null : GameState.CapturePose(actor, baseline);
        if (pose is null)
        {
            Plugin.Print("Could not read a skeleton pose (is the character fully loaded?).");
            return;
        }

        // own race = the character's live skeleton, whatever is loaded on it; another
        // race = its vanilla sklb (its modded skeleton, if any, is not ours to see)
        var skeleton = race == info.RaceSexId && GameState.LiveBodySkeleton() is var live && live != 0
            ? SkeletonSource.FromLive(live)
            : LoadSklb(target.FileRace);
        if (skeleton is null)
        {
            Plugin.Print("Could not load the skeleton for that race.");
            return;
        }

        var vtbl = HavokVtables.InterleavedAnimation(Plugin.SigScanner);
        var tempDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        var bakes = new Dictionary<string, byte[]>();
        var basedOn = new HashSet<string>();
        var customBones = new HashSet<string>(StringComparer.Ordinal);

        var loop = BakeTarget(plugin, modRoot, target, pose, skeleton, asStart: false, tempDir, vtbl, basedOn);
        if (loop is null)
        {
            Plugin.Print("Could not load the animation data.");
            return;
        }

        bakes[target.GamePath] = loop;
        customBones.UnionWith(HavokBaker.ExtraBoneNames);

        // ease-in: the slot's _start transition ends exactly where the loop begins (its
        // frame 0, which differs from the snap when the snap was mid-clip)
        var startPose = pose with
        {
            Locals = pose.Locals.ToDictionary(
                kv => kv.Key,
                kv => HavokBaker.LoopFrame0.TryGetValue(kv.Key, out var f0) ? kv.Value with { Rotation = f0.Rotation, Translation = f0.Translation } : kv.Value,
                StringComparer.Ordinal),
        };
        if (IdleResolver.ResolveStart(family, targetSlot, race, info.AnimVariant, battleFolder) is { } startTarget
            && BakeTarget(plugin, modRoot, startTarget, startPose, skeleton, asStart: true, tempDir, vtbl, basedOn) is { } start)
            bakes[startTarget.GamePath] = start;

        var groupName = GameState.RaceName(race);
        var optionName = $"{GameState.FamilyLabel(family, battleFolder)} {targetSlot}";
        var isNew = !plugin.Penumbra.KnowsMod(plugin.Config.ModDirectory);
        ModWriter.WriteOption(modRoot, plugin.Config.ModDirectory, plugin.Config.ModDisplayName, groupName, optionName, bakes);

        plugin.Config.LastFamily = (int)family;
        plugin.Config.LastSlot = targetSlot;
        plugin.Config.Save();

        Publish(plugin, plugin.Config.ModDirectory, isNew, groupName, optionName);
        var basis = basedOn.Count > 0 ? $" over {string.Join(", ", basedOn)}" : "";
        var custom = customBones.Count > 0 ? $", +{customBones.Count} custom bone{(customBones.Count == 1 ? "" : "s")}" : "";
        Plugin.Print($"Saved {groupName} {optionName}{basis}{custom}.");
    }

    private static SkeletonSource? LoadSklb(ushort race)
    {
        var sklbFile = Plugin.DataManager.GetFile(IdleResolver.SklbPath(race));
        return sklbFile is null ? null : SkeletonSource.FromSklb(SklbFile.HavokData(sklbFile.Data));
    }

    // Bakes over the mod currently replacing the path when the user wants that, falling
    // back to vanilla when the modded file needs bones this skeleton lacks.
    private static byte[]? BakeTarget(Plugin plugin, string modRoot, IdleTarget target, CapturedPose pose, SkeletonSource skeleton, bool asStart, string tempDir, nint vtbl, HashSet<string> basedOn)
    {
        if (plugin.Config.SaveOverModded && ModdedBasis(plugin, modRoot, target.GamePath) is var (moddedBytes, modName) && moddedBytes is not null)
        {
            try
            {
                var baked = HavokBaker.Bake(moddedBytes, skeleton, pose, asStart, tempDir, vtbl);
                basedOn.Add(modName!);
                return baked;
            }
            catch (HavokBaker.SkeletonMismatchException)
            {
                Plugin.Print($"{modName} needs bones your skeleton does not have - used the vanilla animation instead.");
            }
            catch (InvalidDataException ex)
            {
                Plugin.Log.Warning($"modded basis {modName} unreadable: {ex.Message}");
                Plugin.Print($"{modName}'s animation could not be read - used the vanilla animation instead.");
            }
        }

        var papFile = Plugin.DataManager.GetFile(target.GamePath);
        return papFile is null ? null : HavokBaker.Bake(papFile.Data, skeleton, pose, asStart, tempDir, vtbl);
    }

    // The file the character's collection serves for this path, if a mod other than
    // ReAnimate provides it. Our own bake on top is looked past by briefly switching the
    // mod off for the resolve.
    private static (byte[]? Bytes, string? ModName) ModdedBasis(Plugin plugin, string modRoot, string gamePath)
    {
        var ownDir = Path.GetFullPath(Path.Combine(modRoot, plugin.Config.ModDirectory));
        var resolved = plugin.Penumbra.ResolvePlayerPath(gamePath);
        if (resolved is not null && IsInside(resolved, ownDir) && plugin.Penumbra.PlayerCollection() is { } collection)
        {
            var dir = plugin.Config.ModDirectory;
            plugin.Penumbra.SetModEnabled(collection.Id, dir, false);
            try
            {
                resolved = plugin.Penumbra.ResolvePlayerPath(gamePath);
            }
            finally
            {
                plugin.Penumbra.SetModEnabled(collection.Id, dir, true);
            }
        }

        if (resolved is null || string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase)
            || IsInside(resolved, ownDir) || !File.Exists(resolved))
            return (null, null);

        var relative = Path.GetRelativePath(modRoot, resolved).Replace('\\', '/');
        var modName = relative.StartsWith("..") ? Path.GetFileName(Path.GetDirectoryName(resolved) ?? resolved) : relative.Split('/')[0];
        return (File.ReadAllBytes(resolved), modName);
    }

    private static bool IsInside(string path, string dir)
        => Path.GetFullPath(path).StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Register/reload the mod and switch the new option on in the live collection (the
    // meta's DefaultSettings only covers collections with no settings yet).
    public static void Publish(Plugin plugin, string dir, bool isNew, string groupName, string optionName)
    {
        if (isNew)
        {
            plugin.Penumbra.AddMod(dir);
            plugin.Penumbra.SetModPath(dir, $"ReAnimate/{dir}");
        }
        else
        {
            plugin.Penumbra.ReloadMod(dir);
        }

        if (plugin.Penumbra.PlayerCollection() is { } collection)
        {
            if (isNew)
            {
                // scoped to the collection governing THIS character; a fresh mod lighting
                // up in other collections is how someone else's character catches it
                plugin.Penumbra.SetModEnabled(collection.Id, dir, true);
                plugin.Penumbra.SetModPriority(collection.Id, dir, plugin.Config.ModPriority);
                foreach (var other in plugin.Penumbra.Collections().Keys.Where(id => id != collection.Id))
                    plugin.Penumbra.SetModEnabled(other, dir, false);
            }

            var selections = plugin.Penumbra.CurrentSelections(collection.Id, dir);
            var enabled = selections.TryGetValue(groupName, out var current) ? current : [];
            if (!enabled.Contains(optionName, StringComparer.Ordinal))
            {
                enabled.Add(optionName);
                plugin.Penumbra.SetModOptions(collection.Id, dir, groupName, enabled);
            }
        }
        else if (isNew)
        {
            Plugin.Print("Mod created, but no active collection found - enable it in Penumbra yourself.");
        }

        plugin.Penumbra.RedrawPlayer();
    }
}
