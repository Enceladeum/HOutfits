using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HOutfits;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string Command = "/houtfits";

    private readonly WindowSystem _windows = new("HOutfits");
    private readonly MainWindow _main;
    private readonly NpcService _npcs;
    private readonly SlotIconService _slotIcons;

    public Plugin()
    {
        var config   = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var glam     = new GlamourerIpc(PluginInterface);
        var moniker  = new MonikerIpc(PluginInterface);
        var outfits  = new OutfitService(DataManager, Log);
        var bnpcNames = new BNpcNameData(Log);
        var npcs     = new NpcService(DataManager, Log, bnpcNames);
        var npcState = new NpcStateBuilder(glam, Log);
        _slotIcons   = new SlotIconService(Log);
        _main        = new MainWindow(outfits, npcs, npcState, glam, moniker, TextureProvider, _slotIcons, Log, config);
        _npcs        = npcs;

        // Kick the BNpc name fetch. When it finishes, drop the NPC cache so battle
        // NPCs populate without a restart (they need the fetched name mapping).
        bnpcNames.OnRefreshed += () => _npcs.Invalidate();
        bnpcNames.Load();

        _windows.AddWindow(_main);

        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi    += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi  += OpenMain;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the outfit list. Click a set to apply it, or a single piece to add just that piece, via Glamourer.",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.Draw        -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windows.RemoveAllWindows();
        _main.Dispose();
        _slotIcons.Dispose();
    }

    private void OnCommand(string _, string __) => OpenMain();

    private void OpenMain() => _main.IsOpen = true;
}
