using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AutoPartyFinder.Windows;

namespace AutoPartyFinder;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/apf";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    public Configuration Configuration { get; }
    public DutyCatalog Duties { get; }
    public PartyFinderService PartyFinder { get; }
    public readonly WindowSystem WindowSystem = new("AutoPartyFinder");

    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Duties = new DutyCatalog();
        PartyFinder = new PartyFinderService(this);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Auto Party Finder. /apf recruit posts the saved listing. /apf stop ends auto-relist.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("Auto Party Finder loaded. Use /apf to recruit.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);
        PartyFinder.Dispose();
        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void Chat(string message) => ChatGui.Print(message, "APF");

    public void ChatError(string message) => ChatGui.PrintError(message, "APF");

    private void OnCommand(string command, string args)
    {
        var trimmed = args?.Trim() ?? string.Empty;
        if (trimmed.Equals("recruit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("start", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("post", StringComparison.OrdinalIgnoreCase))
        {
            PartyFinder.StartRecruit(enableAutoRelist: true);
            return;
        }

        if (trimmed.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            PartyFinder.Stop(endListing: false);
            return;
        }

        if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            PartyFinder.Stop(endListing: true);
            return;
        }

        ToggleMainUi();
    }
}
