using Dalamud.Configuration;

namespace ReAnimate;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Penumbra mod folder name on disk and its display name in the mod list.
    public string ModDirectory { get; set; } = "ReAnimate";
    public string ModDisplayName { get; set; } = "ReAnimate";

    // Swaps land as a "ReAnimate swaps" option inside the source mod (off = rewrite the
    // mod's own paths in place).
    public bool SwapAsOption { get; set; } = true;

    // High so the bake wins over other animation mods touching the same paths.
    public int ModPriority { get; set; } = 99;

    // Where /reanimate save lands when the current stance isn't bakeable.
    public int LastFamily { get; set; } = -1;
    public byte LastSlot { get; set; }

    // Base the bake on whatever mod currently replaces that idle (top priority in the
    // character's collection, ReAnimate itself excluded) instead of the vanilla file.
    public bool SaveOverModded { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
