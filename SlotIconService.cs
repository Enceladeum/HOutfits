using System;
using System.Collections.Generic;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;

namespace HOutfits;

/// <summary>
/// Provides the generic "empty slot" placeholder icons — the greyed silhouettes
/// FFXIV shows on the character equipment screen (helm outline, glove outline,
/// etc.). These are the same placeholders Glamourer uses for NPC gear that has no
/// obtainable item.
///
/// They're texture PARTS of the character-screen ULD, not normal game icons:
/// load ui/uld/Character.uld, then pull the part for each slot from
/// ui/uld/Character_hr1.tex by index. Part indices are copied from Glamourer's
/// TextureService: Head=19, Body=20, Hands=21, Legs=23, Feet=24, Ears=25,
/// Neck=26, Wrists=27, Ring=28 (L shares R), MainHand=17, OffHand=18.
///
/// Loaded once and cached. If the ULD can't be read (e.g. an incompatible UI
/// mod), the icons are left null and the UI falls back to a text label.
/// </summary>
public sealed class SlotIconService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Dictionary<ApiEquipSlot, IDalamudTextureWrap> _icons = new();
    private bool _loaded;

    public SlotIconService(IPluginLog log)
    {
        _log = log;
    }

    private static readonly (ApiEquipSlot Slot, int Part)[] SlotParts =
    {
        (ApiEquipSlot.Head,    19),
        (ApiEquipSlot.Body,    20),
        (ApiEquipSlot.Hands,   21),
        (ApiEquipSlot.Legs,    23),
        (ApiEquipSlot.Feet,    24),
        (ApiEquipSlot.Ears,    25),
        (ApiEquipSlot.Neck,    26),
        (ApiEquipSlot.Wrists,  27),
        (ApiEquipSlot.RFinger, 28),
        (ApiEquipSlot.LFinger, 28), // shares the ring icon
        (ApiEquipSlot.MainHand, 17),
        (ApiEquipSlot.OffHand, 18),
    };

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            using var uld = Plugin.PluginInterface.UiBuilder.LoadUld("ui/uld/Character.uld");
            if (!uld.Valid)
            {
                _log.Warning("Could not load Character.uld for slot placeholder icons.");
                return;
            }

            foreach (var (slot, part) in SlotParts)
            {
                try
                {
                    var tex = uld.LoadTexturePart("ui/uld/Character_hr1.tex", part);
                    if (tex != null)
                        _icons[slot] = tex;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Could not load placeholder icon for {Slot}.", slot);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Slot placeholder icons unavailable (UI mod conflict?).");
        }
    }

    /// <summary>The placeholder texture for a slot, or null if unavailable.</summary>
    public IDalamudTextureWrap? Get(ApiEquipSlot slot)
    {
        EnsureLoaded();
        return _icons.TryGetValue(slot, out var tex) ? tex : null;
    }

    public void Dispose()
    {
        foreach (var tex in _icons.Values)
            tex.Dispose();
        _icons.Clear();
    }
}
