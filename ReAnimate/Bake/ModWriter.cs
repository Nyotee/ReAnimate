using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReAnimate.Swap;

namespace ReAnimate.Bake;

// The Penumbra mod is organized as one MULTI group per race ("Miqo'te Female") whose
// options are the saved slots ("Doze 2"), so users manage saves straight from Penumbra's
// mod page. The on-disk meta.json is the source of truth: it's read and surgically edited
// (users delete options there), never regenerated. Penumbra caches animations by path, so
// every bake gets a content-hash filename; anything no option references gets swept.
public static class ModWriter
{
    // Penumbra multi groups cap at 32 options; per-race groups stay well under it.
    private const int MaxMultiOptions = 32;

    // Penumbra convention: mod files live in a subfolder, not the mod root.
    private const string FilesDir = "files";

    public static string FileName(string gamePath, byte[] papBytes)
    {
        var r = AnimationCatalog.RaceOf(gamePath);
        var race = r != 0 ? $"c{r:D4}" : "all";
        var hash = Convert.ToHexString(SHA256.HashData(papBytes))[..8].ToLowerInvariant();
        return $"{race}_{Path.GetFileNameWithoutExtension(gamePath)}.{hash}{Path.GetExtension(gamePath)}";
    }

    // Files this plugin wrote (race/all prefix + 8-hex hash); nothing else is ever swept,
    // so living inside someone else's mod folder is safe.
    private static bool IsOwnedFile(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name, @"^(all|c\d{4})_.+\.[0-9a-f]{8}\.(pap|tmb)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // V4 unified meta (groups inline) - the only layout we edit in place. Older mods keep
    // their group_*.json files and get a sibling mod instead.
    public static bool IsUnifiedMeta(string modRoot, string modDir)
    {
        // a missing Groups key is normal: Penumbra omits it for a mod with no option groups
        var meta = ModMeta.Parse(Path.Combine(modRoot, modDir, "meta.json"));
        return meta is not null && (meta["FileVersion"]?.Value<int>() ?? 0) >= 4;
    }

    // Writes the paps, upserts the option, sweeps orphans. Paths in `bakes` may share a
    // file (several game paths -> one pap); identical bytes dedupe by hash name.
    public static void WriteOption(string modRoot, string modDir, string displayName, string groupName, string optionName, Dictionary<string, byte[]> bakes, int groupPriority = 0)
    {
        var dir = Path.Combine(modRoot, modDir);
        Directory.CreateDirectory(dir);
        var files = WriteFiles(dir, bakes);

        var meta = LoadMeta(displayName, dir);
        UpsertOption(meta, groupName, optionName, files, groupPriority);
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToString(Formatting.Indented));

        SweepOrphans(dir, meta);
    }

    // Rewrites an existing option of a (unified) mod: its redirects for `oldGamePaths` go,
    // the new ones come in. Group "" = the default container. False when the option is missing.
    public static bool ReplaceInPlace(string modRoot, string modDir, string groupName, string optionName, IReadOnlyCollection<string> oldGamePaths, Dictionary<string, byte[]> bakes)
    {
        var dir = Path.Combine(modRoot, modDir);
        var meta = LoadMeta(modDir, dir);
        var filesObject = groupName.Length == 0
            ? meta["DefaultData"]?["Files"] as JObject
            : ModMeta.Options(meta)
                .FirstOrDefault(x => x.Group["Name"]?.ToString() == groupName && x.Option["Name"]?.ToString() == optionName)
                .Option?["Files"] as JObject;
        if (filesObject is null)
            return false;

        var files = WriteFiles(dir, bakes);
        foreach (var old in oldGamePaths)
            filesObject.Remove(old);
        foreach (var (gamePath, file) in files)
            filesObject[gamePath] = file;
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToString(Formatting.Indented));
        SweepOrphans(dir, meta);
        return true;
    }

    private static Dictionary<string, string> WriteFiles(string dir, Dictionary<string, byte[]> bakes)
    {
        var filesDir = Path.Combine(dir, FilesDir);
        Directory.CreateDirectory(filesDir);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shared = new HashSet<byte[]>(bakes.Values.GroupBy(b => b, ReferenceEqualityComparer.Instance).Where(g => g.Count() > 1).Select(g => (byte[])g.Key!), ReferenceEqualityComparer.Instance);
        var written = new Dictionary<byte[], string>(ReferenceEqualityComparer.Instance);
        foreach (var (gamePath, bytes) in bakes)
        {
            if (!written.TryGetValue(bytes, out var name))
            {
                // one file for every race when the same bytes serve several paths
                name = shared.Contains(bytes) ? FileName("all/" + Path.GetFileName(gamePath), bytes) : FileName(gamePath, bytes);
                File.WriteAllBytes(Path.Combine(filesDir, name), bytes);
                written[bytes] = name;
            }

            files[gamePath] = $"{FilesDir}\\{name}";
        }

        return files;
    }

    // the mod's meta.json, or a fresh V4 one when there is none (or it is unreadable)
    private static JObject LoadMeta(string displayName, string dir)
    {
        return ModMeta.Parse(Path.Combine(dir, "meta.json")) ?? new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = displayName,
            ["Author"] = "ReAnimate",
            ["Description"] = "Saved poses, managed by the ReAnimate plugin. Toggle or delete saves per option; ReAnimate cleans up files it wrote for options that are gone.",
            ["Version"] = "1.0",
            ["ModTags"] = new JArray("animation", "reanimate"),
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject(),
                ["FileSwaps"] = new JObject(),
                ["Manipulations"] = new JArray(),
            },
            ["Groups"] = new JArray(),
        };
    }

    private static void UpsertOption(JObject meta, string groupName, string optionName, Dictionary<string, string> files, int groupPriority)
    {
        if (meta["Groups"] is not JArray groups)
            meta["Groups"] = groups = [];

        var group = groups.OfType<JObject>().FirstOrDefault(g =>
            string.Equals(g["Name"]?.ToString(), groupName, StringComparison.Ordinal)
            && g["Type"]?.ToString() == "Multi");
        if (group is null)
        {
            group = new JObject
            {
                ["Name"] = groupName,
                ["Type"] = "Multi",
                ["Priority"] = groupPriority,
                ["DefaultSettings"] = 0,
                ["Options"] = new JArray(),
            };
            groups.Add(group);
        }

        if (group["Options"] is not JArray options)
            group["Options"] = options = [];

        var fileMap = new JObject();
        foreach (var (gamePath, file) in files)
            fileMap[gamePath] = file;

        var existing = options.OfType<JObject>().FirstOrDefault(o =>
            string.Equals(o["Name"]?.ToString(), optionName, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing["Files"] = fileMap;
        }
        else
        {
            if (options.Count >= MaxMultiOptions)
                throw new InvalidOperationException($"group '{groupName}' is full ({MaxMultiOptions} options)");

            options.Add(new JObject
            {
                ["Name"] = optionName,
                ["Priority"] = 0,
                ["Files"] = fileMap,
                ["FileSwaps"] = new JObject(),
                ["Manipulations"] = new JArray(),
            });

            // new saves come enabled for collections that have no settings yet
            var bit = 1UL << (options.Count - 1);
            group["DefaultSettings"] = (group["DefaultSettings"]?.Value<ulong>() ?? 0UL) | bit;
        }
    }

    // A user may delete an option in Penumbra and leave its paps behind; anything not
    // referenced by any option or the default container goes.
    private static void SweepOrphans(string dir, JObject meta)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filesObject in ModMeta.FilesObjects(meta))
        {
            foreach (var file in filesObject.Properties())
                referenced.Add(file.Value.ToString().Replace('\\', '/'));
        }

        // only files this plugin wrote (root included: pre-subfolder builds wrote paps there)
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (!IsOwnedFile(Path.GetFileName(file)))
                continue;
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (!referenced.Contains(relative))
            {
                Plugin.Log.Debug($"sweeping orphaned {relative}");
                File.Delete(file);
            }
        }
    }
}
