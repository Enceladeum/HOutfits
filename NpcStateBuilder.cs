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
/// Customize: the state's Customize block is per-field { "Value", "Apply" },
/// keyed per CustomizeIndex (36 options): NOT one key per byte of the 26-byte
/// customize array. Several bytes pack multiple options behind bit masks, so each
/// key's value is `customizeByte &amp; mask` (masked, not shifted), per Penumbra's
/// CustomizeArray.Get. See CustomizeMap. We set Apply=true on the fields we write
/// (and only in the "appearance" apply modes).
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
        // Weapons sit in the SAME Equipment object under these keys, with the
        // exact per-slot schema armor uses (ItemId/Apply/Stain/Stain2/ApplyStain),
        // so SetSlot handles them unchanged.
        [ApiEquipSlot.MainHand] = "MainHand",
        [ApiEquipSlot.OffHand]  = "OffHand",
    };

    // Glamourer Customize JObject key -> (byte index in the 26-byte customize
    // array, bit mask within that byte).
    //
    // IMPORTANT: the Customize block is keyed per CustomizeIndex (36 options), NOT
    // per byte (26). Five bytes pack several options behind bit masks: byte 7
    // (Highlights), byte 12 (the seven FacialFeatures + LegacyTattoo), byte 16
    // (EyeShape + SmallIris), byte 19 (Mouth + Lipstick) and byte 24 (FacePaint +
    // FacePaintReversed). Writing a whole byte into one of those keys pushes an
    // out-of-range value at it (e.g. EyeShape is masked 0x7F, so a byte with the
    // high bit set exceeds it and the eye shape silently fails to apply), and
    // leaves the co-resident options never written at all.
    //
    // Values are masked but NOT shifted down, matching Penumbra's
    // CustomizeArray.Get: `Data[offset] & mask`. So SmallIris is 0 or 128,
    // FacialFeature2 is 0 or 2, etc.
    //
    // Table copied verbatim from Penumbra.GameData CustomizeIndex.ToByteAndMask.
    private static readonly (string Key, int Byte, byte Mask)[] CustomizeMap =
    {
        ("Race",              0,  0xFF),
        ("Gender",            1,  0xFF),
        ("BodyType",          2,  0xFF),
        ("Height",            3,  0xFF),
        ("Clan",              4,  0xFF),
        ("Face",              5,  0xFF),
        ("Hairstyle",         6,  0xFF),
        ("Highlights",        7,  0x80),
        ("SkinColor",         8,  0xFF),
        ("EyeColorRight",     9,  0xFF),
        ("HairColor",         10, 0xFF),
        ("HighlightsColor",   11, 0xFF),
        ("FacialFeature1",    12, 0x01),
        ("FacialFeature2",    12, 0x02),
        ("FacialFeature3",    12, 0x04),
        ("FacialFeature4",    12, 0x08),
        ("FacialFeature5",    12, 0x10),
        ("FacialFeature6",    12, 0x20),
        ("FacialFeature7",    12, 0x40),
        ("LegacyTattoo",      12, 0x80),
        ("TattooColor",       13, 0xFF),
        ("Eyebrows",          14, 0xFF),
        ("EyeColorLeft",      15, 0xFF),
        ("EyeShape",          16, 0x7F),
        ("SmallIris",         16, 0x80),
        ("Nose",              17, 0xFF),
        ("Jaw",               18, 0xFF),
        ("Mouth",             19, 0x7F),
        ("Lipstick",          19, 0x80),
        ("LipColor",          20, 0xFF),
        ("MuscleMass",        21, 0xFF),
        ("TailShape",         22, 0xFF),
        ("BustSize",          23, 0xFF),
        ("FacePaint",         24, 0x7F),
        ("FacePaintReversed", 24, 0x80),
        ("FacePaintColor",    25, 0xFF),
    };

    public enum Mode { Both, AppearanceOnly, GearOnly }

    /// <summary>
    /// Apply an NPC to the local player. Returns the Glamourer result code, or
    /// null if the local state couldn't be read.
    ///
    /// <paramref name="includeWeapons"/> is opt-in and experimental: NPC weapons
    /// are written as custom-model CustomItemIds in the MainHand/OffHand slots.
    /// Glamourer may refuse a weapon whose type doesn't match the player's class,
    /// so this can be a no-op for some weapons even when everything is encoded
    /// correctly. Left off by default.
    /// </summary>
    public GlamourerApiEc? Apply(NpcEntry npc, Mode mode, bool includeAccessories, bool includeWeapons)
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
        {
            WriteEquipment(equip, npc, includeAccessories, includeWeapons);
            if (includeWeapons)
                LogWeapons(npc);
        }

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
            if (IsWeapon(piece.Slot))
                _log.Information("NPC weapon piece {Slot} -> CustomItemId={Id} (FullEquipType={Fet}).",
                    piece.Slot, CustomItemId(piece), EquipTypeFor(piece.Slot));
        }

        return _glam.ApplyState(state, 0, ApplyFlag.Equipment);
    }

    /// <summary>The two weapon (hand) slots. Mirrored by MainWindow's dimming logic.</summary>
    public static bool IsWeapon(ApiEquipSlot slot)
        => slot is ApiEquipSlot.MainHand or ApiEquipSlot.OffHand;

    private static void WriteEquipment(JObject equip, NpcEntry npc, bool includeAccessories, bool includeWeapons)
    {
        foreach (var piece in npc.Pieces)
        {
            if (!includeAccessories && OutfitService.IsAccessory(piece.Slot))
                continue;
            if (!includeWeapons && IsWeapon(piece.Slot))
                continue;
            if (!SlotKey.TryGetValue(piece.Slot, out var key) || equip[key] is not JObject slot)
                continue;
            SetSlot(slot, piece);
        }
    }

    // Diagnostic for the experimental weapon path: log the exact CustomItemId we
    // hand Glamourer for each weapon slot, so an in-game test can confirm the
    // value it receives (and, if a weapon doesn't apply, whether it was even
    // written). Information level while the path is unverified; demote to Debug
    // once weapon apply is confirmed working.
    private void LogWeapons(NpcEntry npc)
    {
        foreach (var piece in npc.Pieces)
        {
            if (!IsWeapon(piece.Slot))
                continue;
            _log.Information(
                "NPC weapon {Slot}: Set={Set} Type={Type} Variant={Variant} Dye={Dye}/{Dye2} -> CustomItemId={Id} (FullEquipType={Fet}).",
                piece.Slot, piece.Model, piece.Secondary, piece.Variant, piece.Dye, piece.Dye2,
                CustomItemId(piece), EquipTypeFor(piece.Slot));
        }
    }

    // FullEquipType values, verified verbatim against the LIVE Glamourer's own
    // Penumbra.GameData (its FullEquipType enum + FullEquipTypeExtensions.ToSlot):
    // Unknown=0, Head=1, Body=2, Hands=3, Legs=4, Feet=5, Ears=6, Neck=7,
    // Wrists=8, Finger=9 (both rings share Finger).
    //
    // Weapons: the SPECIFIC category (Sword=12, Bow=14, Gun=23, ...) is derived
    // from a weapon's item / equip-category data, which NPC sheet gear does not
    // carry — so we genuinely cannot know it. Penumbra ships two sentinels for
    // exactly this case: UnknownMainhand=66 and UnknownOffhand=67. Its own
    // ToSlot() maps them to MainHand/OffHand (and UnknownMainhand.IsWeapon() is
    // true), so a CustomItemId stamped with them routes to the correct hand and
    // the model loads from Set/Type/Variant (bits 0-39) — the mesh does NOT
    // depend on the category byte. Using 0/Unknown here would make ToSlot()
    // return EquipSlot.Unknown and the weapon would silently route nowhere
    // (verified by decompiling ToSlot's default arm).
    private static ulong EquipTypeFor(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head     => 1,
        ApiEquipSlot.Body     => 2,
        ApiEquipSlot.Hands    => 3,
        ApiEquipSlot.Legs     => 4,
        ApiEquipSlot.Feet     => 5,
        ApiEquipSlot.Ears     => 6,
        ApiEquipSlot.Neck     => 7,
        ApiEquipSlot.Wrists   => 8,
        ApiEquipSlot.RFinger  => 9,
        ApiEquipSlot.LFinger  => 9,
        ApiEquipSlot.MainHand => 66, // FullEquipType.UnknownMainhand
        ApiEquipSlot.OffHand  => 67, // FullEquipType.UnknownOffhand
        _                     => 0,
    };

    private const ulong CustomFlag = 1ul << 48;

    // Build the CustomItemId Glamourer stores for NPC (non-item) gear. Encoding
    // verified verbatim against the live Glamourer's Penumbra.GameData
    // CustomItemId(model, secondary, variant, type) constructor:
    //   model | (secondary<<16) | (variant<<32) | (type<<40) | CustomFlag.
    // Armor sets secondary = 0, so this stays byte-identical to the armor-only
    // build; weapons carry the model "Type" in secondary.
    private static ulong CustomItemId(NpcPiece piece)
        => piece.Model
         | ((ulong)piece.Secondary << 16)
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
        foreach (var (key, byteIdx, mask) in CustomizeMap)
        {
            if (byteIdx >= c.Length)
                continue;
            if (cust[key] is not JObject field)
                continue; // key absent from this state (e.g. non-human): leave it

            // Masked, not shifted: matches Penumbra's CustomizeArray.Get.
            field["Value"] = (byte)(c[byteIdx] & mask);
            field["Apply"] = true;
        }
    }
}
