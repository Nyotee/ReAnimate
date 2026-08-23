using Newtonsoft.Json.Linq;
using ReAnimate.Bake;
using ReAnimate.Game;

namespace ReAnimate.Swap;

// One pap redirect inside an installed Penumbra mod. Group is "" for the default container.
public sealed record ModPap(string Group, string Option, string GamePath, string FilePath);

// One path pattern of a mod, backed by one file per race (often the same file for all).
public sealed record ModPart(string RelKey, IReadOnlyDictionary<ushort, string> Files, IReadOnlyList<string> GamePaths)
{
    public bool Exists => Files.Values.All(File.Exists);

    // The file to mirror onto a target race: that race's own, else up the race tree,
    // else whatever there is.
    public string FileFor(ushort race)
    {
        foreach (var r in IdleResolver.FallbackChain(race))
        {
            if (Files.TryGetValue(r, out var f))
                return f;
        }

        return Files.Values.First();
    }
}

// One ANIMATION of a mod: a plain file, or a loop+start pair (an idle slot ships both);
// same pattern = same animation, whatever the files. Parts are keyed by kind.
public sealed record ModAnim(string Group, string Option, IReadOnlyDictionary<string, ModPart> Parts)
{
    // the loop (or the single file): what a plain emote target plays
    public ModPart Main { get; } = MainOf(Parts);
    public string Display { get; } = Describe(MainOf(Parts), Parts.ContainsKey("loop"));
    public string Label => Group.Length == 0 ? Option : $"{Group} / {Option}";
    public ModPart? PartFor(string kind) => kind.Length == 0 ? Main : Parts.GetValueOrDefault(kind);
    public string RelKey => Main.RelKey;
    public IReadOnlyDictionary<ushort, string> Files => Main.Files;
    public IEnumerable<string> GamePaths => Parts.Values.SelectMany(p => p.GamePaths);
    public bool Exists => Parts.Values.All(p => p.Exists);

    private static ModPart MainOf(IReadOnlyDictionary<string, ModPart> parts)
        => parts.GetValueOrDefault("loop") ?? parts.GetValueOrDefault("") ?? parts.Values.First();

    private static string Describe(ModPart main, bool pair)
    {
        var display = AnimationCatalog.Describe(main.GamePaths[0]);
        return pair ? display.Replace(" (loop)", "") : display;
    }
}

// Reads a mod's animation redirects off its own json (see ModMeta for the layouts).
public static class ModScanner
{
    public static List<ModPap> Scan(string modRoot, string modDir)
    {
        var dir = Path.Combine(modRoot, modDir);
        var result = new List<ModPap>();
        if (!Directory.Exists(dir))
            return result;

        var meta = ModMeta.Parse(Path.Combine(dir, "meta.json"));
        if (meta?["DefaultData"]?["Files"] is JObject defaults)
            AddFiles(result, dir, "", "Default", defaults);
        else if (ModMeta.Parse(Path.Combine(dir, "default_mod.json"))?["Files"] is JObject legacyDefaults)
            AddFiles(result, dir, "", "Default", legacyDefaults);

        var groups = meta?["Groups"] is JArray inline
            ? inline.OfType<JObject>()
            : Directory.GetFiles(dir, "group_*.json").Order().Select(ModMeta.Parse).OfType<JObject>();
        foreach (var group in groups)
        {
            var groupName = group["Name"]?.ToString() ?? "?";
            foreach (var option in ModMeta.OptionsOf(group))
            {
                if (option["Files"] is JObject files)
                    AddFiles(result, dir, groupName, option["Name"]?.ToString() ?? "", files);
            }
        }

        return result;
    }

    // The mod's .tmb redirects (game path -> file on disk), any option.
    public static Dictionary<string, string> ScanTmbs(string modRoot, string modDir)
    {
        var dir = Path.Combine(modRoot, modDir);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return result;

        foreach (var filesObject in ModMeta.JsonFiles(dir).Select(ModMeta.Parse).OfType<JObject>().SelectMany(ModMeta.FilesObjects))
        {
            foreach (var prop in filesObject.Properties())
            {
                if (!prop.Name.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase))
                    continue;
                var file = ModMeta.FileOf(dir, prop);
                if (File.Exists(file))
                    result.TryAdd(prop.Name.Replace('\\', '/'), file);
            }
        }

        return result;
    }

    // Mods that ship at least one swappable animation: a cheap text probe rejects most, the
    // rest get the real scan. No game calls (the catalog is warmed first), so it runs off-thread.
    public static HashSet<string> ModsWithAnimations(string modRoot, IEnumerable<string> modDirs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in modDirs)
        {
            var dir = Path.Combine(modRoot, d);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                if (ModMeta.JsonFiles(dir).Any(f => File.ReadAllText(f).Contains(".pap\"", StringComparison.OrdinalIgnoreCase))
                    && Group(Scan(modRoot, d)).Count > 0)
                    result.Add(d);
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    // Body animations only (paths the catalog understands), one row per option + animation
    // (a loop+start pair is one row).
    public static List<ModAnim> Group(IEnumerable<ModPap> paps)
    {
        var parts = new Dictionary<(string Group, string Option, string Rel), ModPart>();
        foreach (var g in paps.GroupBy(p => (p.Group, p.Option, Rel: AnimationCatalog.RelKey(p.GamePath)))
                     .Where(g => g.Key.Rel is not null && AnimationCatalog.IsKnown(g.Key.Rel)))
        {
            var files = new Dictionary<ushort, string>();
            foreach (var p in g)
                files.TryAdd(AnimationCatalog.RaceOf(p.GamePath), p.FilePath);
            parts[(g.Key.Group, g.Key.Option, g.Key.Rel!)] = new ModPart(g.Key.Rel!, files, g.Select(p => p.GamePath).Distinct().ToList());
        }

        var rows = parts
            .GroupBy(kv => (kv.Key.Group, kv.Key.Option, Base: KeyKind.Split(kv.Key.Rel).Base))
            .Select(g => new ModAnim(g.Key.Group, g.Key.Option, g.ToDictionary(kv => KeyKind.Split(kv.Key.Rel).Kind, kv => kv.Value)));
        return rows.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddFiles(List<ModPap> result, string dir, string group, string option, JObject files)
    {
        foreach (var prop in files.Properties())
        {
            if (prop.Name.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                result.Add(new ModPap(group, option, prop.Name, ModMeta.FileOf(dir, prop)));
        }
    }
}
