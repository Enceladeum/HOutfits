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

    /// <summary>
    /// Outfit sets tab: also list sets recovered from item names (e.g. "Allagan of
    /// Striking"), which the game doesn't list as sets. On by default; turn off to
    /// see only the game's own sets.
    /// </summary>
    public bool IncludeGroupedSets { get; set; } = true;

    /// <summary>Loose gear tab: the selected slot chip (index into <see cref="GearSlot"/>).</summary>
    public int LooseSlot { get; set; } = 0;

    /// <summary>
    /// Loose gear tab: collapse pieces that share an identical look (role variants
    /// such as "of Fending" / "of Casting") into one tile. On by default.
    /// </summary>
    public bool LooseHideDuplicateLooks { get; set; } = true;

    /// <summary>
    /// Loose gear tab: also list pieces that belong to a set (the game's own sets
    /// and the name-grouped ones), so a single per-slot list and search covers both
    /// set and non-set gear. Off by default, so the tab keeps showing only pieces
    /// that belong to no set. Set pieces still live on the Outfit sets tab too.
    /// </summary>
    public bool LooseIncludeSetItems { get; set; } = false;

    /// <summary>
    /// Loose gear and Weapons tabs: draw the icon tiles and the hover preview at twice
    /// the default size (80px tiles, 224px preview). Toggled by the "Large icons"
    /// tickbox in the header; off is the default size. Game icons are a fixed
    /// resolution, so the large size is upscaled (slightly soft).
    /// </summary>
    public bool LargeIcons { get; set; } = false;
}
