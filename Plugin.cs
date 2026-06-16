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

    public Plugin()
    {
        var glam     = new GlamourerIpc(PluginInterface);
        var outfits  = new OutfitService(DataManager, Log);
        _main        = new MainWindow(outfits, glam, TextureProvider, Log);

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
    }

    private void OnCommand(string _, string __) => OpenMain();

    private void OpenMain() => _main.IsOpen = true;
}
