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
}
