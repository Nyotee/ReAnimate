using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace ReAnimate.Ipc;

// Thin Penumbra IPC layer (Penumbra.Api breaking version 5). Every call is guarded: with
// Penumbra missing or mid-reload the operation reports failure instead of throwing.
public sealed class PenumbraIpc(IDalamudPluginInterface pi, IPluginLog log)
{
    private readonly ICallGateSubscriber<string> getModDirectory =
        pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory");

    private readonly ICallGateSubscriber<string, int> addMod =
        pi.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");

    private readonly ICallGateSubscriber<string, string, int> reloadMod =
        pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");

    private readonly ICallGateSubscriber<Guid, string, string, bool, int> trySetMod =
        pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");

    private readonly ICallGateSubscriber<Guid, string, string, int, int> trySetModPriority =
        pi.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5");

    private readonly ICallGateSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)> getCollectionForObject =
        pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");

    private readonly ICallGateSubscriber<int, int, object?> redrawObject =
        pi.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");

    private readonly ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)> getCurrentModSettings =
        pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");

    private readonly ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int> trySetModSettings =
        pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5");

    private readonly ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?> availableModSettings =
        pi.GetIpcSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>("Penumbra.GetAvailableModSettings.V5");

    private readonly ICallGateSubscriber<string, string, string, int> setModPath =
        pi.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5");

    // bare labels, no ".V5": that is how Penumbra registers these two
    private readonly ICallGateSubscriber<string, string> resolvePlayerPath =
        pi.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath");

    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList =
        pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");

    private readonly ICallGateSubscriber<Dictionary<Guid, string>> getCollections =
        pi.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5");

    private ICallGateSubscriber<string, string, object?>? modMovedHook;
    private Action<string, string>? modMovedCallback;

    public string? ModDirectory
    {
        get
        {
            try
            {
                var dir = getModDirectory.InvokeFunc();
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch (Exception ex)
            {
                // the UI polls this; a missing Penumbra must not flood the log
                log.Debug($"Penumbra.GetModDirectory failed: {ex.Message}");
                return null;
            }
        }
    }

    private long availCheckTick;
    private bool availCached;

    // Penumbra reachable, with a 3s cache for per-frame UI callers.
    public bool AvailableCached
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - availCheckTick > 3000)
            {
                availCheckTick = now;
                availCached = ModDirectory is not null;
            }

            return availCached;
        }
    }

    public int AddMod(string modDirectoryName)
    {
        try
        {
            return addMod.InvokeFunc(modDirectoryName);
        }
        catch (Exception ex)
        {
            log.Error($"Penumbra.AddMod failed: {ex.Message}");
            return -1;
        }
    }

    public int ReloadMod(string modDirectoryName)
    {
        try
        {
            return reloadMod.InvokeFunc(modDirectoryName, "");
        }
        catch (Exception ex)
        {
            // Penumbra's own post-reload handlers can throw after the reload already landed.
            log.Warning($"Penumbra.ReloadMod('{modDirectoryName}'): {ex.Message}");
            return -1;
        }
    }

    // Whether Penumbra has this mod registered (targeted probe, no full mod list).
    public bool KnowsMod(string modDirectoryName)
    {
        try
        {
            return availableModSettings.InvokeFunc(modDirectoryName, "") is not null;
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.GetAvailableModSettings failed: {ex.Message}");
            return false;
        }
    }

    // The collection actually governing the player right now (individual assignments included).
    public (Guid Id, string Name)? PlayerCollection()
    {
        try
        {
            var (valid, _, effective) = getCollectionForObject.InvokeFunc(0);
            if (valid && effective.Id != Guid.Empty)
                return effective;
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.GetCollectionForObject failed: {ex.Message}");
        }

        return null;
    }

    public int SetModEnabled(Guid collection, string modDirectoryName, bool enabled)
    {
        try
        {
            return trySetMod.InvokeFunc(collection, modDirectoryName, "", enabled);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.TrySetMod failed: {ex.Message}");
            return -1;
        }
    }

    public int SetModPriority(Guid collection, string modDirectoryName, int priority)
    {
        try
        {
            return trySetModPriority.InvokeFunc(collection, modDirectoryName, "", priority);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.TrySetModPriority failed: {ex.Message}");
            return -1;
        }
    }

    // Cosmetic: file the mod under a folder in Penumbra's list ("ReAnimate/..." sort path).
    public void SetModPath(string modDirectoryName, string sortPath)
    {
        try
        {
            setModPath.InvokeFunc(modDirectoryName, "", sortPath);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.SetModPath failed: {ex.Message}");
        }
    }

    // group -> selected option names in the collection, empty when unset/unknown.
    public Dictionary<string, List<string>> CurrentSelections(Guid collection, string modDirectoryName)
    {
        try
        {
            var (ec, data) = getCurrentModSettings.InvokeFunc(collection, modDirectoryName, "", false);
            return ec == 0 && data is { } d ? d.Item3 : [];
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.GetCurrentModSettings failed: {ex.Message}");
            return [];
        }
    }

    public int SetModOptions(Guid collection, string modDirectoryName, string groupName, IReadOnlyList<string> optionNames)
    {
        try
        {
            return trySetModSettings.InvokeFunc(collection, modDirectoryName, "", groupName, optionNames);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.TrySetModSettings failed: {ex.Message}");
            return -1;
        }
    }

    public void RedrawPlayer()
    {
        try
        {
            redrawObject.InvokeAction(0, 0);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.RedrawObject failed: {ex.Message}");
        }
    }

    // What the player's collection actually serves for a game path: a full disk path
    // when a mod replaces it, the game path itself when not.
    public string? ResolvePlayerPath(string gamePath)
    {
        try
        {
            return resolvePlayerPath.InvokeFunc(gamePath);
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.ResolvePlayerPath failed: {ex.Message}");
            return null;
        }
    }

    // Installed mods: directory name -> display name.
    public Dictionary<string, string> ModList()
    {
        try
        {
            return getModList.InvokeFunc() ?? [];
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.GetModList failed: {ex.Message}");
            return [];
        }
    }

    // Every collection (id -> name); empty when Penumbra cannot answer.
    public Dictionary<Guid, string> Collections()
    {
        try
        {
            return getCollections.InvokeFunc() ?? [];
        }
        catch (Exception ex)
        {
            log.Warning($"Penumbra.GetCollections failed: {ex.Message}");
            return [];
        }
    }

    // Mods are identified by directory NAME in all of Penumbra's IPC (the meta GUID is
    // unused), so renames must be followed via this event. Bare label, no ".V5".
    public bool TryHookModMoved(Action<string, string> onMoved)
    {
        if (modMovedHook is not null)
            return true;

        try
        {
            var sub = pi.GetIpcSubscriber<string, string, object?>("Penumbra.ModMoved");
            sub.Subscribe(onMoved);
            modMovedHook = sub;
            modMovedCallback = onMoved;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"ModMoved hook unavailable: {ex.Message}");
            return false;
        }
    }

    public void UnhookModMoved()
    {
        if (modMovedHook is null || modMovedCallback is null)
            return;

        try
        {
            modMovedHook.Unsubscribe(modMovedCallback);
        }
        catch (Exception ex)
        {
            log.Warning($"ModMoved unhook failed: {ex.Message}");
        }
        finally
        {
            modMovedHook = null;
            modMovedCallback = null;
        }
    }
}
