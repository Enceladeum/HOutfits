using Dalamud.Configuration;

namespace HOutfits;

/// <summary>
/// Persisted plugin settings. Dalamud serialises this to the plugin's config
/// folder; load it with <c>PluginInterface.GetPluginConfig()</c> and write it
/// back with <c>PluginInterface.SavePluginConfig(this)</c> whenever a value
/// changes.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// When false, applying a WHOLE set skips the accessory slots (earrings,
    /// necklace, bracelets, rings) so a set drops onto your glamour without
    /// disturbing hidden or separately-glamoured accessories. Default true, so
    /// the out-of-the-box behaviour is unchanged (apply everything). Clicking an
    /// individual accessory icon still applies that one piece regardless.
    /// </summary>
    public bool IncludeAccessories { get; set; } = true;

    /// <summary>
    /// NPC tab: which regions of an NPC to apply. 0 = both (appearance + gear),
    /// 1 = appearance only (face/body), 2 = gear only. Mirrors Glamourer's NPC
    /// panel options.
    /// </summary>
    public int NpcApplyMode { get; set; } = 0;

    /// <summary>
    /// NPC tab: when true and Moniker is installed, applying an NPC also pushes
    /// the NPC's name to the local nameplate via Moniker.
    /// </summary>
    public bool NpcApplyName { get; set; } = false;

    /// <summary>
    /// NPC tab: when true, applying a WHOLE NPC also copies its main-hand and
    /// off-hand weapons. Opt-in and experimental: weapons are written as
    /// custom-model appearances, and Glamourer can refuse a weapon whose type
    /// doesn't match your current class, so it may silently do nothing for some
    /// weapons. Off by default. Clicking an individual weapon icon still applies
    /// that one piece regardless of this setting.
    /// </summary>
    public bool NpcIncludeWeapons { get; set; } = false;
}
