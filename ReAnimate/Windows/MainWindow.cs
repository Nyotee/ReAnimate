using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ReAnimate.Bake;
using ReAnimate.Game;
using ReAnimate.Swap;

namespace ReAnimate.Windows;

// Two tabs: "Idle2Pose" (save the pose on screen into an idle slot) and "Animation Swap"
// (retarget any installed mod's animation at a different emote).
public sealed class MainWindow : Window
{
    private static readonly Vector4 ColGood = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 ColBad = new(0.9f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 ColDim = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ColWarn = new(0.95f, 0.8f, 0.3f, 1f);

    private readonly Plugin plugin;

    // idle tab
    private ushort race;
    private EmoteController.PoseType family = EmoteController.PoseType.Idle;
    private int slot;

    // idle tab slot cache: (family, race, battle folder) -> slots that resolve
    private (EmoteController.PoseType Family, ushort Race, string? Folder) slotsKey;
    private List<int> slotsCache = [];

    // swap tab
    private Dictionary<string, string> mods = [];
    private HashSet<string> animMods = new(StringComparer.OrdinalIgnoreCase);
    private Task<HashSet<string>>? scan;
    private readonly Dictionary<uint, ISharedImmediateTexture?> icons = [];
    private string modFilter = "";
    private string? selectedMod;
    private List<ModAnim> paps = [];
    private Dictionary<string, List<string>> swaps = [];
    private readonly Dictionary<int, AnimTarget> targetChoice = [];
    private readonly Dictionary<string, AnimationCatalog.AnimFlags?> flagCache = new(StringComparer.OrdinalIgnoreCase);
    private string targetFilter = "";
    private bool selectedUnified;
    private (ModAnim Anim, AnimTarget Target)? pendingSwap;
    private bool openConfirm;

    public MainWindow(Plugin plugin) : base("ReAnimate###ReAnimateMain")
    {
        this.plugin = plugin;
        Size = new Vector2(720, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // Default the pickers to what's on screen right now.
    public override void OnOpen()
    {
        var info = GameState.ReadPlayerInfo();
        race = info?.RaceSexId ?? 101;
        if (info is not null && GameState.BakeableFamilies.Contains(info.Family))
            family = info.Family;
        slot = GameState.LiveSlot(family) ?? 0;
        RefreshMods();
    }

    public override void Draw()
    {
        var penumbraOk = plugin.Penumbra.AvailableCached;
        if (penumbraOk)
            ImGui.TextColored(ColGood, "Penumbra connected.");
        else
            ImGui.TextColored(ColBad, "Penumbra not found - required!");

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Idle2Pose"))
        {
            DrawIdleTab(penumbraOk);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Animation Swap"))
        {
            DrawSwapTab(penumbraOk);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---------------------------------------------------------------- idle

    private void DrawIdleTab(bool penumbraOk)
    {
        var info = GameState.ReadPlayerInfo();
        if (info is null)
        {
            ImGui.TextColored(ColDim, "Log in with a character to save poses.");
            return;
        }

        if (!GameState.PlayableRaces.Contains(race))
            race = info.RaceSexId;

        var onScreen = Enum.IsDefined(info.Family) ? $"{GameState.FamilyName(info.Family)} {info.Slot}" : "no idle detected";
        ImGui.TextColored(ColDim, $"On screen now: {onScreen}"
            + (Plugin.ClientState.IsGPosing ? "  (GPose: snapping the GPose target)" : ""));

        ImGui.Separator();

        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Race", RaceLabel(race, info)))
        {
            foreach (var r in GameState.PlayableRaces)
            {
                if (ImGui.Selectable(RaceLabel(r, info), r == race) && r != race)
                {
                    race = r;
                    slot = LiveSlotFor(family, r, info) ?? 0;
                }
            }

            ImGui.EndCombo();
        }

        var battleFolder = GameState.BattleFolder(info.ClassJob);
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Pose type", GameState.FamilyLabel(family, battleFolder)))
        {
            foreach (var f in GameState.BakeableFamilies)
            {
                if (ImGui.Selectable(GameState.FamilyLabel(f, battleFolder), f == family) && f != family)
                {
                    family = f;
                    slot = LiveSlotFor(f, race, info) ?? 0;
                }
            }

            ImGui.EndCombo();
        }

        if (family == EmoteController.PoseType.WeaponDrawn && battleFolder is null)
        {
            ImGui.TextColored(ColBad, "Your current class has no weapon-drawn idles.");
            return;
        }

        // Only slots whose loop pap actually resolves for the chosen race are offered;
        // races differ in how many idles they ship.
        if (slotsKey != (family, race, battleFolder))
        {
            slotsKey = (family, race, battleFolder);
            slotsCache = AvailableSlots(info, battleFolder);
        }

        var slots = slotsCache;
        if (slots.Count == 0)
        {
            ImGui.TextColored(ColBad, "No animation files found for this pose type on that race.");
            return;
        }

        if (!slots.Contains(slot))
            slot = slots[0];

        // "(current)" only for the stance the player is actually in, on their own race
        var live = LiveSlotFor(family, race, info);
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Pose", SlotLabel(slot, live)))
        {
            foreach (var s in slots)
            {
                if (ImGui.Selectable(SlotLabel(s, live), s == slot))
                    slot = s;
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!penumbraOk);
        if (ImGui.Button($"Save pose  →  {IdleResolver.Label(family, (byte)slot, race, battleFolder)}", new Vector2(440, 30)))
            BakeService.Bake(plugin, family, (byte)slot, race);
        ImGui.EndDisabled();

        var overModded = plugin.Config.SaveOverModded;
        if (ImGui.Checkbox("Save over modded idle", ref overModded))
        {
            plugin.Config.SaveOverModded = overModded;
            plugin.Config.Save();
        }
    }

    private static byte? LiveSlotFor(EmoteController.PoseType f, ushort r, PlayerInfo info)
        => r == info.RaceSexId ? GameState.LiveSlot(f) : null;

    private List<int> AvailableSlots(PlayerInfo info, string? battleFolder)
    {
        var result = new List<int>();
        for (var s = 0; s < GameState.SlotCount(family); s++)
        {
            if (IdleResolver.Resolve(family, (byte)s, race, info.AnimVariant, battleFolder, out _) is not null)
                result.Add(s);
        }

        return result;
    }

    private static string RaceLabel(ushort r, PlayerInfo info)
        => r == info.RaceSexId ? $"{GameState.RaceName(r)} (you)" : GameState.RaceName(r);

    private static string SlotLabel(int s, byte? live)
        => s == live ? $"{s} (current)" : $"{s}";

    // ---------------------------------------------------------------- swap
    private AnimationCatalog.AnimFlags? SourceFlags(ModAnim source)
    {
        var file = source.Files.Values.FirstOrDefault();
        if (file is null)
            return null;
        if (!flagCache.TryGetValue(file, out var f))
        {
            f = File.Exists(file) ? AnimationCatalog.FlagsOfPap(File.ReadAllBytes(file)) : null;
            flagCache[file] = f;
        }

        return f;
    }

    // Searchable popup with game icons, listing only targets of the source's stance kind
    // (base emotes for a base source, u_/s_/j_ for a variant source). Returns the pick.
    private AnimTarget? DrawTargetPicker(ModAnim source, AnimTarget? current)
    {
        ImGui.SetNextWindowSize(new Vector2(520, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##picker"))
            return null;

        AnimTarget? picked = null;
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##targetfilter", "Search emotes...", ref targetFilter, 64);
        var sourceFlags = SourceFlags(source);
        ImGui.TextColored(ColDim, $"Play {source.Display} as:");
        ImGui.TextColored(ColGood, "green = OK");
        ImGui.SameLine();
        ImGui.TextColored(ColBad, "red = loses loop / expressions");

        ImGui.BeginChild("##targetlist", new Vector2(0, 0), false);
        var rowHeight = 40f;
        foreach (var t in AnimationCatalog.TargetsFor(source.RelKey))
        {
            if (targetFilter.Length > 0 && !t.Name.Contains(targetFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var pos = ImGui.GetCursorPos();
            if (ImGui.Selectable($"##{t.Name}/{t.Variants[0].Key}", current is not null && current.Name == t.Name, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
            {
                picked = t;
                ImGui.CloseCurrentPopup();
            }

            // icon + name + key drawn over the selectable
            ImGui.SetCursorPos(pos + new Vector2(4, 4));
            var wrap = Icon(t.Icon);
            if (wrap is not null)
                ImGui.Image(wrap.Handle, new Vector2(32, 32));
            else
                ImGui.Dummy(new Vector2(32, 32));
            ImGui.SameLine();
            var textPos = ImGui.GetCursorPos();
            ImGui.TextColored(AnimationCatalog.Loses(sourceFlags, t) ? ColBad : ColGood, t.Name);
            ImGui.SetCursorPos(new Vector2(textPos.X, textPos.Y + ImGui.GetTextLineHeight()));
            ImGui.TextColored(ColDim, string.Join("  ·  ", t.Variants.Select(v => v.Key)));
            ImGui.SetCursorPosY(pos.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y);
        }

        ImGui.EndChild();
        ImGui.EndPopup();
        return picked;
    }

    // The mod probe touches every mod folder; it runs off-thread and the list fills in.
    private void RefreshMods()
    {
        mods = plugin.Penumbra.ModList();
        var root = plugin.Penumbra.ModDirectory;
        if (selectedMod is not null && !mods.ContainsKey(selectedMod))
            selectedMod = null;
        if (root is null)
            return;
        var dirs = mods.Keys.ToList();
        AnimationCatalog.Warm();
        scan = Task.Run(() => ModScanner.ModsWithAnimations(root, dirs));
    }

    private void PollScan()
    {
        if (scan is not { IsCompleted: true } done)
            return;
        scan = null;
        if (done.IsCompletedSuccessfully)
            animMods = done.Result;
        else
            Plugin.Log.Warning($"mod scan failed: {done.Exception?.GetBaseException().Message}");
    }

    // Game icons can be missing (placeholder ids); a miss is remembered, never rethrown.
    // The shared texture is cached, the wrap is fetched per frame (it is disposed at frame end).
    private Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? Icon(uint id)
    {
        if (id == 0)
            return null;
        if (!icons.TryGetValue(id, out var shared))
        {
            try
            {
                shared = Plugin.Textures.GetFromGameIcon(new GameIconLookup(id));
            }
            catch (Exception)
            {
                shared = null;
            }

            icons[id] = shared;
        }

        return shared?.GetWrapOrDefault();
    }

    private void SelectMod(string dir)
    {
        selectedMod = dir;
        targetChoice.Clear();
        var root = plugin.Penumbra.ModDirectory;
        paps = root is null ? [] : ModScanner.Group(ModScanner.Scan(root, dir));
        selectedUnified = root is not null && ModWriter.IsUnifiedMeta(root, dir);
        pendingSwap = null;
        flagCache.Clear();
        RefreshSwaps();
    }

    private string SelectedModName => mods.GetValueOrDefault(selectedMod!, selectedMod!);

    private void RefreshSwaps()
    {
        var root = plugin.Penumbra.ModDirectory;
        swaps = root is null || selectedMod is null ? [] : SwapService.ExistingSwaps(plugin, root, selectedMod, SelectedModName);
    }

    // The one placement that rewrites the mod's own redirects instead of adding an option
    // (older mod layouts get a sibling mod, which stays toggleable either way).
    private bool Destructive => !plugin.Config.SwapAsOption && selectedUnified;

    private void ApplySwap(ModAnim anim, AnimTarget target)
    {
        var rewritten = Destructive;
        SwapService.Apply(plugin, selectedMod!, SelectedModName, anim, target);
        // a rewrite changed what the mod replaces, so the list has to be read again
        if (rewritten)
            SelectMod(selectedMod!);
        else
            RefreshSwaps();
    }

    private void DrawSwapTab(bool penumbraOk)
    {
        if (!penumbraOk)
        {
            ImGui.TextColored(ColDim, "Needs Penumbra to list mods.");
            return;
        }

        // the question everyone asks: where did my swap go, and how do I take it back
        if (Destructive)
            ImGui.TextColored(ColBad, "Swaps rewrite the mod you pick. That cannot be undone.");
        else
            ImGui.TextColored(ColWarn, "Swaps go into the mod you pick, as a toggle. Untick it in Penumbra to undo.");
        ImGui.Separator();

        PollScan();

        // left: installed mods that carry animations
        ImGui.BeginChild("##swapmods", new Vector2(260, 0), true);
        ImGui.SetNextItemWidth(-36);
        ImGui.InputTextWithHint("##modfilter", "Search mods...", ref modFilter, 128);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Sync))
            RefreshMods();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rescan installed mods");

        ImGui.BeginChild("##swapmodlist", new Vector2(0, 0), false);
        if (scan is not null)
            ImGui.TextColored(ColDim, "Scanning mods...");
        foreach (var (dir, name) in mods.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (dir == plugin.Config.ModDirectory || !animMods.Contains(dir))
                continue;
            if (modFilter.Length > 0 && !name.Contains(modFilter, StringComparison.OrdinalIgnoreCase)
                && !dir.Contains(modFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ImGui.Selectable($"{name}##{dir}", dir == selectedMod))
                SelectMod(dir);
        }

        ImGui.EndChild();
        ImGui.EndChild();

        ImGui.SameLine();

        // right: that mod's animations
        ImGui.BeginChild("##swapanims", new Vector2(0, 0), true);
        var asOption = plugin.Config.SwapAsOption;
        if (ImGui.Checkbox("Add as a mod option", ref asOption))
        {
            plugin.Config.SwapAsOption = asOption;
            plugin.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: the swap is an option inside the mod, untick it in Penumbra whenever.\nOff: the mod's own paths are rewritten, which cannot be undone.");
        if (selectedMod is null)
        {
            ImGui.TextColored(ColDim, "Pick a mod to see the animations it replaces.");
        }
        else if (paps.Count == 0)
        {
            ImGui.TextColored(ColDim, "This mod replaces no animations.");
        }
        else
        {
            DrawPapTable();
        }

        ImGui.EndChild();
    }

    private void DrawPapTable()
    {
        if (!ImGui.BeginTable("##paps", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Replaces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Play it as", ImGuiTableColumnFlags.WidthFixed, 230);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        for (var i = 0; i < paps.Count; i++)
        {
            var pap = paps[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pap.Label);

            ImGui.TableNextColumn();
            var label = pap.Files.Count > 1 ? $"{pap.Display}  ({pap.Files.Count} races)" : pap.Display;
            if (pap.Exists)
                ImGui.TextUnformatted(label);
            else
                ImGui.TextColored(ColBad, $"{label}  (file missing)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", pap.GamePaths));
            if (swaps.TryGetValue(pap.Display, out var playsAs))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColGood, $"→ now plays as {string.Join(", ", playsAs)}");
            }

            ImGui.TableNextColumn();
            var chosenTarget = targetChoice.GetValueOrDefault(i);
            if (ImGui.Button(chosenTarget?.Name ?? "choose an emote...", new Vector2(-1, 0)))
            {
                targetFilter = "";
                ImGui.OpenPopup("##picker");
            }

            if (DrawTargetPicker(pap, chosenTarget) is { } picked)
                targetChoice[i] = picked;

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!pap.Exists || chosenTarget is null);
            if (ImGui.Button("Swap"))
            {
                if (Destructive)
                    (pendingSwap, openConfirm) = ((pap, chosenTarget!), true);
                else
                    ApplySwap(pap, chosenTarget!);
            }
            ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
        DrawSwapConfirm();
    }

    // Rewriting someone's installed mod cannot be taken back, so it asks first.
    private void DrawSwapConfirm()
    {
        if (pendingSwap is not { } pending)
            return;

        // opened once, never per frame, or Escape would just reopen it
        if (openConfirm)
        {
            ImGui.OpenPopup("###confirmswap");
            openConfirm = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(440, 0)); // height 0 = fit the text, width wraps it
        if (!ImGui.BeginPopupModal("Heads up###confirmswap", ref open, ImGuiWindowFlags.None))
        {
            pendingSwap = null;
            return;
        }

        ImGui.TextColored(ColBad, "This cannot be undone.");
        ImGui.TextWrapped($"\"Add as a mod option\" is off, so {SelectedModName} gets rewritten: {pending.Anim.Display} stops playing where it does now and plays as {pending.Target.Name} instead. Getting it back means reinstalling the mod.");
        ImGui.Spacing();
        if (ImGui.Button("Rewrite the mod", new Vector2(150, 0)))
        {
            ApplySwap(pending.Anim, pending.Target);
            pendingSwap = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            pendingSwap = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
