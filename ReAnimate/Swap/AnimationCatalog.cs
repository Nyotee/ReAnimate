using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using ReAnimate.Game;

namespace ReAnimate.Swap;

// One timeline key of an emote and the race-agnostic file keys the game requests for it
// ("bt_common/emote/dance.pap"; per-weapon keys expand to every battle folder). Kind is
// "loop" / "start" for the two halves of an idle slot, "" for a plain emote key.
public sealed record AnimVariant(string Key, IReadOnlyList<string> RelKeys, string Kind = "");

// A mod animation is a loop+start PAIR or a single file: the trailing _loop/_start of a
// key is its kind, the rest is the animation.
public static class KeyKind
{
    public static (string Base, string Kind) Split(string relKey)
    {
        var stem = Path.ChangeExtension(relKey, null);
        if (stem.EndsWith("_loop", StringComparison.OrdinalIgnoreCase))
            return (stem[..^5], "loop");
        if (stem.EndsWith("_start", StringComparison.OrdinalIgnoreCase))
            return (stem[..^6], "start");
        return (stem, "");
    }
}

// A swappable target: an emote (all its body keys - an emote often has a targeted and an
// untargeted one, and the game picks) or an idle slot. Built once from the Emote sheet.
public sealed record AnimTarget(string Name, uint Icon, IReadOnlyList<AnimVariant> Variants)
{
    public override string ToString() => Name;
}

public static partial class AnimationCatalog
{
    [GeneratedRegex(@"^chara/human/c(\d{4})/animation/a\d{4}/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex HumanPap();

    // Stance variants of one emote share a base name behind a prefix: laugh, j_laugh
    // (ground sit), s_laugh (chair), u_laugh (upper body), b_/l_ likewise.
    private static readonly string[] VariantPrefixes = ["j_", "s_", "u_", "l_", "b_"];

    public static string VariantPrefix(string key)
    {
        var file = key[(key.LastIndexOf('/') + 1)..];
        return VariantPrefixes.FirstOrDefault(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    // Targets for a source: every emote that has keys of the source's stance variant, with
    // only those keys (a base animation lands on base keys, a u_ one on u_ keys).
    public static IEnumerable<AnimTarget> TargetsFor(string sourceRelKey)
    {
        var prefix = VariantPrefix(sourceRelKey);
        foreach (var t in Targets)
        {
            var variants = t.Variants.Where(v => string.Equals(VariantPrefix(v.Key), prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (variants.Count > 0)
                yield return t with { Variants = variants };
        }
    }

    // 246325 is a placeholder icon id with no texture behind it (VFXEditor maps it the same way).
    private static uint IconOf(uint icon) => icon == 246325 ? 405u : icon;

    // Per-animation flags the game keeps in its MotionTimeline sheet, keyed by the pap
    // animation name: looping, lip sync, blinking. What a swap can keep or lose.
    public readonly record struct AnimFlags(bool Loop, bool Lip, bool Blink);

    private static Dictionary<string, AnimFlags>? flags;
    private static readonly Dictionary<string, AnimFlags?> variantFlags = new(StringComparer.OrdinalIgnoreCase);

    public static AnimFlags? FlagsOfName(string animName)
    {
        if (flags is null)
        {
            flags = new Dictionary<string, AnimFlags>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Plugin.DataManager.GetExcelSheet<MotionTimeline>())
            {
                var file = row.Filename.ExtractText();
                if (!string.IsNullOrEmpty(file))
                    flags.TryAdd(file, new AnimFlags(row.IsLoop, row.IsLipEnable, row.IsBlinkEnable));
            }
        }

        return flags.TryGetValue(animName, out var f) ? f : null;
    }

    // Flags of a pap's main (type 0) animation.
    public static AnimFlags? FlagsOfPap(byte[] papBytes)
    {
        try
        {
            var pap = Formats.PapFile.Read(papBytes);
            for (var i = 0; i < pap.AnimCount; i++)
            {
                if (pap.AnimType(i) == 0 && FlagsOfName(pap.AnimName(i)) is { } f)
                    return f;
            }

            return pap.AnimCount > 0 ? FlagsOfName(pap.AnimName(0)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static AnimFlags? FlagsOfVariant(AnimVariant variant)
    {
        if (variantFlags.TryGetValue(variant.Key, out var cached))
            return cached;
        var path = variant.RelKeys.SelectMany(GamePaths).FirstOrDefault();
        var file = path is null ? null : Plugin.DataManager.GetFile(path);
        var result = file is null ? null : FlagsOfPap(file.Data);
        variantFlags[variant.Key] = result;
        return result;
    }

    // Hard rule: the source has a feature the target cannot play (a loop into a one-shot,
    // expressions into none) = a loss, whatever else the target adds. Unknown flags on
    // either side (a name the game's sheet doesn't list) have nothing to lose.
    public static bool Loses(AnimFlags? source, AnimFlags? target)
    {
        if (source is not { } s || target is not { } t)
            return false;
        return (s.Loop && !t.Loop) || (s.Lip && !t.Lip) || (s.Blink && !t.Blink);
    }

    // Any key of the emote losing = a loss. The source's loop is what gets compared, so an
    // idle slot's one-shot _start half is left out (it would always read as a lost loop).
    public static bool Loses(AnimFlags? source, AnimTarget target)
        => target.Variants.Where(v => v.Kind != "start").Any(v => Loses(source, FlagsOfVariant(v)));

    private static List<AnimTarget>? targets;
    private static Dictionary<string, string>? names;

    public static IReadOnlyList<AnimTarget> Targets
    {
        get
        {
            Warm();
            return targets!;
        }
    }

    // "chara/human/c0801/animation/a0001/bt_common/emote/dance.pap" -> (801, "bt_common/emote/dance.pap")
    private static (ushort Race, string Rel)? Parse(string gamePath)
    {
        var m = HumanPap().Match(gamePath.Replace('\\', '/'));
        return m.Success ? (ushort.Parse(m.Groups[1].Value), m.Groups[2].Value.ToLowerInvariant()) : null;
    }

    public static string? RelKey(string gamePath) => Parse(gamePath)?.Rel;

    public static ushort RaceOf(string gamePath) => Parse(gamePath)?.Race ?? 0;

    // What a game path plays as, for humans: "Dance (/dance)", "Standing Idle 3 (loop)".
    public static string Describe(string gamePath)
    {
        Warm();
        var rel = RelKey(gamePath);
        return rel is not null && names!.TryGetValue(rel, out var n) ? n : rel ?? gamePath;
    }

    // Only animations a player can invoke (an emote, an idle slot) are swappable; a mod's
    // other paps (bt_common/resident/action.pap and such bundles) are not listed at all.
    public static bool IsKnown(string relKey)
    {
        Warm();
        return names!.ContainsKey(relKey);
    }

    // Builds once; call on the main thread before a background scan reads the catalog.
    public static void Warm()
    {
        if (names is null)
            Build();
    }

    // Every race's existing vanilla file for a rel key - races without their own request
    // a parent's path, so redirecting all existing ones covers everyone.
    public static IEnumerable<string> GamePaths(string relKey)
    {
        foreach (var race in GameState.PlayableRaces)
        {
            var path = $"chara/human/c{race:D4}/animation/a0001/{relKey}";
            if (Plugin.DataManager.FileExists(path))
                yield return path;
        }
    }

    private static void Build()
    {
        var list = new List<AnimTarget>();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var emotes = Plugin.DataManager.GetExcelSheet<Emote>();
        var poseIcon = emotes
            .Where(e => e.TextCommand.ValueNullable is { } c
                        && (c.Command.ExtractText() is "/changepose" or "/cpose"
                            || c.Alias.ExtractText() is "/changepose" or "/cpose"
                            || c.ShortCommand.ExtractText() is "/changepose" or "/cpose"))
            .Select(e => IconOf(e.Icon))
            .FirstOrDefault();

        // idle slots first so their names win over the generic cpose emote rows; the
        // weapon-drawn slots are served at every weapon folder like per-weapon emotes
        foreach (var family in GameState.BakeableFamilies)
        {
            var folders = family == EmoteController.PoseType.WeaponDrawn ? GameState.AllBattleFolders : ["bt_common"];
            for (byte slot = 0; slot < GameState.MaxSlots; slot++)
            {
                var variants = new List<AnimVariant>();
                var slotLabel = $"{GameState.FamilyName(family)} {slot}";
                foreach (var (file, kind) in IdleResolver.FileKeys(family, slot))
                {
                    var rels = folders.Select(f => $"{f}/{file}.pap").Where(r => GamePaths(r).Any()).ToList();
                    if (rels.Count == 0)
                        continue;
                    foreach (var r in rels)
                        map.TryAdd(r, $"{GameState.FamilyLabel(family, r[..r.IndexOf('/')])} {slot} ({kind})");

                    variants.Add(new AnimVariant(file, rels, kind));
                }

                if (variants.Count > 0)
                    list.Add(new AnimTarget(family == EmoteController.PoseType.WeaponDrawn ? $"{slotLabel}, all weapons" : slotLabel, poseIcon, variants));
            }
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var emote in emotes)
        {
            var emoteName = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(emoteName))
                continue;

            var command = emote.TextCommand.ValueNullable?.Command.ExtractText();
            var display = string.IsNullOrEmpty(command) ? emoteName : $"{emoteName} ({command})";
            var variants = new List<AnimVariant>();
            foreach (var timeline in emote.ActionTimeline)
            {
                if (timeline.ValueNullable is not { } row)
                    continue;
                var key = row.Key.ExtractText();
                if (string.IsNullOrEmpty(key) || !seenKeys.Add(key))
                    continue;

                // LoadType 2 = bt_common, 1 = per weapon folder, 0 = facial (face skeleton, not ours)
                var rels = row.LoadType switch
                {
                    2 => [$"bt_common/{key}.pap"],
                    1 => GameState.AllBattleFolders.Select(f => $"{f}/{key}.pap").ToList(),
                    _ => new List<string>(),
                };
                var existing = rels.Where(r => GamePaths(r).Any()).ToList();
                if (existing.Count == 0)
                    continue;
                variants.Add(new AnimVariant(key, existing));
                foreach (var r in existing)
                    map.TryAdd(r, display);
            }

            if (variants.Count > 0)
                list.Add(new AnimTarget(display, IconOf(emote.Icon), variants));
        }

        targets = list;
        names = map;
        Plugin.Log.Info($"animation catalog: {list.Count} targets");
    }
}
