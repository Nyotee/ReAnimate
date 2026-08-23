using Newtonsoft.Json.Linq;

namespace ReAnimate.Bake;

// Read side of a Penumbra mod's json, shared by the scanner, the writer and the swap
// service: one guarded parser, one list of redirect-bearing files, one walk per layout.
public static class ModMeta
{
    // meta.json (V4, groups inline) plus the pre-unification default_mod.json / group_*.json
    public static IEnumerable<string> JsonFiles(string dir)
    {
        foreach (var name in new[] { "meta.json", "default_mod.json" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                yield return path;
        }

        foreach (var path in Directory.GetFiles(dir, "group_*.json").Order())
            yield return path;
    }

    public static JObject? Parse(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"could not parse {path}: {ex.Message}");
            return null;
        }
    }

    public static IEnumerable<(JObject Group, JObject Option)> Options(JObject meta)
        => (meta["Groups"] as JArray)?.OfType<JObject>().SelectMany(g => OptionsOf(g).Select(o => (g, o))) ?? [];

    public static IEnumerable<JObject> OptionsOf(JObject group)
        => (group["Options"] as JArray)?.OfType<JObject>() ?? [];

    // every "Files" redirect map in a json, whatever the layout
    public static IEnumerable<JObject> FilesObjects(JObject json)
        => json.Descendants().OfType<JProperty>().Where(p => p.Name == "Files" && p.Value is JObject).Select(p => (JObject)p.Value);

    // the on-disk file behind one redirect entry
    public static string FileOf(string dir, JProperty redirect)
        => Path.Combine(dir, redirect.Value.ToString().Replace('/', Path.DirectorySeparatorChar));
}
