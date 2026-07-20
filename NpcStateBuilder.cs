using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace HOutfits;

/// <summary>
/// Turns an <see cref="NpcEntry"/> into a Glamourer state and applies it via
/// ApplyState. NPC gear has no item id, so this cannot use SetItem — it writes
/// the NPC's appearance into a full state JObject and pushes that.
///
/// Strategy: clone the local player's current state (from GetState — a JObject
/// Glamourer definitely accepts, proven by a GetState/ApplyState round-trip), then overwrite
/// only the fields we're changing. Starting from a known-good template avoids
/// reconstructing Glamourer's full schema and guarantees every key it expects is
/// present.
///
/// Equipment: each slot object carries an "ItemId". Glamourer's CustomItemId can
/// encode an NPC model/variant appearance (that's how its own NPC tab paints
/// "9161-1"). We set the slot's ItemId from the NPC's packed model value.
///
/// Customize: the state's Customize block is per-field { "Value", "Apply" }. We
/// overwrite each field's Value from the NPC's 26-byte customize array, using the
/// index order verified against Glamourer's FromEnpcBase. We set Apply=true only
/// on the fields we write (and only in the "appearance" apply modes).
/// </summary>
public sealed class NpcStateBuilder
{
    private readonly GlamourerIpc _glam;
    private readonly IPluginLog _log;

    public NpcStateBuilder(GlamourerIpc glam, IPluginLog log)
    {
        _glam = glam;
        _log  = log;
    }

    // Glamourer Equipment slot key names, in ApiEquipSlot terms. These match the
    // state's Equipment object keys seen in the GetState dump.
    private static readonly Dictionary<ApiEquipSlot, string> SlotKey = new()
    {
        [ApiEquipSlot.Head]    = "Head",
        [ApiEquipSlot.Body]    = "Body",
        [ApiEquipSlot.Hands]   = "Hands",
        [ApiEquipSlot.Legs]    = "Legs",
        [ApiEquipSlot.Feet]    = "Feet",
        [ApiEquipSlot.Ears]    = "Ears",
        [ApiEquipSlot.Neck]    = "Neck",
        [ApiEquipSlot.Wrists]  = "Wrists",
        [ApiEquipSlot.RFinger] = "RFinger",
        [ApiEquipSlot.LFinger] = "LFinger",
    };

    // Customize array index -> the Glamourer Customize JObject key. Order verified
    // against Glamourer's FromEnpcBase (indices 0..25). Keys are Glamourer's
    // CustomizeIndex names as they appear in the GetState dump. A key not present
    // in the live state is simply skipped (its NPC value is not written, template
    // value stays) — no crash.
    private static readonly string[] CustomizeKey =
    {
        "Race",            // 0
        "Gender",          // 1
        "BodyType",        // 2
        "Height",          // 3
        "Clan",            // 4  (ENpcBase Tribe)
        "Face",            // 5
        "Hairstyle",       // 6
        "Highlights",      // 7
        "SkinColor",       // 8
        "EyeColorRight",   // 9  (heterochromia)
        "HairColor",       // 10
        "HighlightsColor", // 11
        "FacialFeature1",  // 12
        "TattooColor",     // 13
        "Eyebrows",        // 14
        "EyeColorLeft",    // 15
        "EyeShape",        // 16
        "Nose",            // 17
        "Jaw",             // 18
        "Mouth",           // 19
        "LipColor",        // 20
        "MuscleMass",      // 21 (BustOrTone1)
        "TailShape",       // 22 (ExtraFeature1)
        "BustSize",        // 23 (ExtraFeature2OrBust)
        "FacePaint",       // 24
        "FacePaintColor",  // 25
    };

    public enum Mode { Both, AppearanceOnly, GearOnly }

    /// <summary>
    /// Apply an NPC to the local player. Returns the Glamourer result code, or
    /// null if the local state couldn't be read.
    /// </summary>
    public GlamourerApiEc? Apply(NpcEntry npc, Mode mode, bool includeAccessories)
    {
        var state = _glam.GetState(0);
        if (state is null)
        {
            _log.Warning("NPC apply: GetState(0) returned null (no character loaded?).");
            return null;
        }

        var doGear       = mode is Mode.Both or Mode.GearOnly;
        var doAppearance = mode is Mode.Both or Mode.AppearanceOnly;

        if (doGear && state["Equipment"] is JObject equip)
            WriteEquipment(equip, npc, includeAccessories);

        if (doAppearance && state["Customize"] is JObject cust)
            WriteCustomize(cust, npc);

        var flags = mode switch
        {
            Mode.GearOnly       => ApplyFlag.Equipment,
            Mode.AppearanceOnly => ApplyFlag.Customization,
            _                   => ApplyFlag.Equipment | ApplyFlag.Customization,
        };

        return _glam.ApplyState(state, 0, flags);
    }

    /// <summary>Apply a single NPC gear piece (additive), via ApplyState with only that slot changed.</summary>
    public GlamourerApiEc? ApplyPiece(NpcEntry npc, NpcPiece piece)
    {
        var state = _glam.GetState(0);
        if (state is null)
            return null;

        if (state["Equipment"] is JObject equip && SlotKey.TryGetValue(piece.Slot, out var key)
            && equip[key] is JObject slot)
        {
            SetSlot(slot, piece);
        }

        return _glam.ApplyState(state, 0, ApplyFlag.Equipment);
    }

    private static void WriteEquipment(JObject equip, NpcEntry npc, bool includeAccessories)
    {
        foreach (var piece in npc.Pieces)
        {
            if (!includeAccessories && OutfitService.IsAccessory(piece.Slot))
                continue;
            if (!SlotKey.TryGetValue(piece.Slot, out var key) || equip[key] is not JObject slot)
                continue;
            SetSlot(slot, piece);
        }
    }

    // FullEquipType values (from Penumbra.GameData): Unknown=0, Head=1, Body=2,
    // Hands=3, Legs=4, Feet=5, Ears=6, Neck=7, Wrists=8, Finger=9. Rings share
    // Finger.
    private static ulong EquipTypeFor(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head    => 1,
        ApiEquipSlot.Body    => 2,
        ApiEquipSlot.Hands   => 3,
        ApiEquipSlot.Legs    => 4,
        ApiEquipSlot.Feet    => 5,
        ApiEquipSlot.Ears    => 6,
        ApiEquipSlot.Neck    => 7,
        ApiEquipSlot.Wrists  => 8,
        ApiEquipSlot.RFinger => 9,
        ApiEquipSlot.LFinger => 9,
        _                    => 0,
    };

    private const ulong CustomFlag = 1ul << 48;

    // Build the CustomItemId Glamourer stores for NPC (non-item) gear. Encoding
    // copied verbatim from Penumbra.GameData CustomItemId(model, secondary,
    // variant, type): model | (secondary<<16) | (variant<<32) | (type<<40) |
    // CustomFlag. Armor has secondary = 0.
    private static ulong CustomItemId(NpcPiece piece)
        => piece.Model
         | ((ulong)piece.Variant << 32)
         | (EquipTypeFor(piece.Slot) << 40)
         | CustomFlag;

    // Overwrite one Equipment slot object with the NPC piece's appearance. The
    // ItemId is a CustomItemId encoding model/variant (NOT the raw column value);
    // stains carry the NPC dyes.
    private static void SetSlot(JObject slot, NpcPiece piece)
    {
        slot["ItemId"]     = CustomItemId(piece);
        slot["Apply"]      = true;
        slot["Stain"]      = piece.Dye;
        slot["Stain2"]     = piece.Dye2;
        slot["ApplyStain"] = true;
    }

    private static void WriteCustomize(JObject cust, NpcEntry npc)
    {
        var c = npc.Customize;
        for (var i = 0; i < CustomizeKey.Length && i < c.Length; i++)
        {
            var key = CustomizeKey[i];
            if (cust[key] is JObject field)
            {
                field["Value"] = c[i];
                field["Apply"] = true;
            }
        }
    }
}
