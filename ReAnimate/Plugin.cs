using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ReAnimate.Bake;
using ReAnimate.Game;
using ReAnimate.Ipc;
using ReAnimate.Windows;

namespace ReAnimate;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;

    internal Configuration Config { get; }
    internal PenumbraIpc Penumbra { get; }

    private const string CommandName = "/reanimate";
    private const string CommandAlias = "/reanim";
    private const string Usage = "/reanim save <idle | sitting | groundsit | doze | weapon> <0-6>";
    private readonly WindowSystem windows = new("ReAnimate");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Penumbra = new PenumbraIpc(PluginInterface, Log);

        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        // posing happens in GPose; the window must not vanish there
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = $"or /reanim to open the menu\n{Usage}",
        });
        Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand) { ShowInHelp = false });

        // mods are identified by folder name in Penumbra's IPC; follow renames
        Penumbra.TryHookModMoved(OnModMoved);
    }

    private void OnModMoved(string oldDirectory, string newDirectory)
    {
        if (!string.Equals(oldDirectory, Config.ModDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        Config.ModDirectory = newDirectory;
        Config.Save();
        Log.Info($"Penumbra mod folder renamed, now tracking '{newDirectory}'");
    }

    private void ToggleWindow() => mainWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        try
        {
            Dispatch(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "reanimate command failed");
            Print($"Failed: {ex.Message}");
        }
    }

    private void Dispatch(string args)
    {
        var normalized = args.Trim().ToLowerInvariant()
            .Replace("ground sit", "groundsit").Replace("ground-sit", "groundsit");
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            ToggleWindow();
            return;
        }

        EmoteController.PoseType? family = null;
        byte? slot = null;
        string? action = null;

        foreach (var token in tokens)
        {
            switch (token)
            {
                case "idle" or "stand" or "standing":
                    family = EmoteController.PoseType.Idle;
                    break;
                case "sit" or "sitting" or "chair":
                    family = EmoteController.PoseType.Sit;
                    break;
                case "groundsit" or "ground":
                    family = EmoteController.PoseType.GroundSit;
                    break;
                case "doze" or "sleep" or "bed":
                    family = EmoteController.PoseType.Doze;
                    break;
                case "weapon" or "drawn" or "battle" or "combat":
                    family = EmoteController.PoseType.WeaponDrawn;
                    break;
                case "save" or "help":
                    action = token;
                    break;
                default:
                    if (byte.TryParse(token, out var n) && n < GameState.MaxSlots)
                        slot = n;
                    else
                        action = "help";
                    break;
            }
        }

        switch (action)
        {
            case "help":
                Print($"{Usage} - no args opens the menu. Manage saves in the Penumbra mod.");
                return;
            case "save" when family is null:
                BakeService.SaveCurrent(this);
                return;
        }

        if (CurrentOr(family) is { } bakeFamily)
            BakeService.Bake(this, bakeFamily, slot);
    }

    // Explicit family, else the one the player is in right now (if bakeable).
    private static EmoteController.PoseType? CurrentOr(EmoteController.PoseType? family)
    {
        if (family is not null)
            return family;

        var info = GameState.ReadPlayerInfo();
        if (info is not null && GameState.BakeableFamilies.Contains(info.Family))
            return info.Family;

        Print(info is null
            ? "Log in with a character first."
            : $"'{GameState.FamilyName(info.Family)}' cannot be saved - name a pose type (idle, sitting, groundsit, doze, weapon).");
        return null;
    }

    internal static void Print(string message) => Chat.Print($"[ReAnimate] {message}");

    public void Dispose()
    {
        Penumbra.UnhookModMoved();
        Commands.RemoveHandler(CommandName);
        Commands.RemoveHandler(CommandAlias);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        windows.RemoveAllWindows();
        Config.Save();
    }
}
