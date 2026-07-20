using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;

namespace HOutfits;

/// <summary>
/// One NPC row for the NPC tab. Carries the 26-byte customize array and per-slot
/// gear appearance needed to build a Glamourer state for ApplyState.
/// </summary>
public sealed record NpcEntry(
    uint RowId,
    string Name,
    string RaceName,
    string ClanName,
    bool IsFemale,
    IReadOnlyList<NpcPiece> Pieces,
    byte[] Customize,
    string SearchText);

/// <summary>
/// One NPC gear slot as APPEARANCE (model + variant + dyes), not an item id —
/// this is what Glamourer shows as "9161-1". Pieces use generic placeholders in
/// the UI (NPC gear often has no obtainable item / icon).
/// </summary>
public sealed record NpcPiece(
    ApiEquipSlot Slot,
    ushort Model,
    byte Variant,
    byte Dye,
    byte Dye2,
    string Label);

/// <summary>
/// Builds the NPC list from BOTH event NPCs (ENpcBase + ENpcResident) and battle
/// NPCs (BNpcBase + BNpcCustomize + the fetched BNpcName mapping), mirroring
/// Glamourer's enumeration. All game data via stock Lumina; the one piece not in
/// the sheets — the BNpcBase->BNpcName association — comes from BNpcNameData.
///
/// Mappings taken verbatim from Glamourer's NpcCustomizeSet:
/// - Customize is a fixed 26-byte array at indices 0..25 (same column set on
///   ENpcBase and BNpcCustomize).
/// - Equipment per slot packs model+variant; dyes are separate.
/// - For event NPCs, gear comes from the NpcEquip REFERENCE when the inline model
///   columns are empty (ModelBody == 0 && ModelLegs == 0), else the inline
///   columns — this is Glamourer's exact rule and the fix for NPCs that were
///   showing empty slots.
/// - Battle NPCs always take gear from their NpcEquip reference.
/// - Human gate: ModelChara.Type == 1.
/// </summary>
public sealed class NpcService
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly BNpcNameData _bnpcNames;

    private List<NpcEntry>? _cache;

    public NpcService(IDataManager data, IPluginLog log, BNpcNameData bnpcNames)
    {
        _data      = data;
        _log       = log;
        _bnpcNames = bnpcNames;
    }

    public IReadOnlyList<NpcEntry> Npcs => _cache ??= BuildNpcs();

    /// <summary>Clear the cache so the list rebuilds (e.g. after the BNpc name fetch completes).</summary>
    public void Invalidate() => _cache = null;

    private List<NpcEntry> BuildNpcs()
    {
        var result = new List<NpcEntry>();
        var raceSheet  = _data.GetExcelSheet<Race>();
        var tribeSheet = _data.GetExcelSheet<Tribe>();

        BuildEventNpcs(result, raceSheet, tribeSheet);
        BuildBattleNpcs(result, raceSheet, tribeSheet);

        result = Deduplicate(result);
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _log.Information("Loaded {Count} named human NPCs (event + battle, deduped).", result.Count);
        return result;
    }

    /// <summary>
    /// Appearance-aware dedup, matching Glamourer: group by name, and within each
    /// name-group drop entries whose appearance (customize + equipment) is
    /// identical to an earlier one. NPCs that share a name but differ in look are
    /// KEPT (they're genuinely different) — the NPC id in the row distinguishes
    /// them. Byte-identical repeats collapse to one.
    /// </summary>
    private static List<NpcEntry> Deduplicate(List<NpcEntry> entries)
    {
        var result = new List<NpcEntry>(entries.Count);
        foreach (var group in entries.GroupBy(e => e.Name))
        {
            var kept = new List<NpcEntry>();
            foreach (var entry in group)
            {
                var dup = false;
                foreach (var k in kept)
                {
                    if (AppearanceEquals(entry, k))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    kept.Add(entry);
            }
            result.AddRange(kept);
        }
        return result;
    }

    private static bool AppearanceEquals(NpcEntry a, NpcEntry b)
    {
        if (!a.Customize.AsSpan().SequenceEqual(b.Customize))
            return false;
        if (a.Pieces.Count != b.Pieces.Count)
            return false;
        for (var i = 0; i < a.Pieces.Count; i++)
        {
            var pa = a.Pieces[i];
            var pb = b.Pieces[i];
            if (pa.Slot != pb.Slot || pa.Model != pb.Model || pa.Variant != pb.Variant
                || pa.Dye != pb.Dye || pa.Dye2 != pb.Dye2)
                return false;
        }
        return true;
    }

    // --- Event NPCs (ENpcBase + ENpcResident) --------------------------------
    private void BuildEventNpcs(List<NpcEntry> result,
        Lumina.Excel.ExcelSheet<Race>? raceSheet, Lumina.Excel.ExcelSheet<Tribe>? tribeSheet)
    {
        var eNpcBase = _data.GetExcelSheet<ENpcBase>();
        var eNpcName = _data.GetExcelSheet<ENpcResident>();
        if (eNpcBase is null || eNpcName is null)
        {
            _log.Error("Could not load ENpcBase / ENpcResident sheets.");
            return;
        }

        foreach (var row in eNpcBase)
        {
            if (row.RowId == 0)
                continue;
            if (row.ModelChara.ValueNullable is not { } mc || mc.Type != 1)
                continue;

            var resident = eNpcName.GetRowOrDefault(row.RowId);
            var name = resident?.Singular.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var customize = ReadCustomizeFromENpc(row);
            var isFemale  = customize[1] == 1;

            // Gear: prefer the NpcEquip reference when the inline columns are
            // empty (Glamourer's rule). This is the fix for empty-slot NPCs.
            List<NpcPiece> pieces;
            if (row.NpcEquip.RowId != 0 && row.NpcEquip.ValueNullable is { } equip
                && row.ModelBody == 0 && row.ModelLegs == 0)
                pieces = ReadEquipFromNpcEquip(equip);
            else
                pieces = ReadEquipFromENpc(row);

            result.Add(MakeEntry(row.RowId, name, customize, isFemale, pieces, raceSheet, tribeSheet,
                row.Race.RowId, row.Tribe.RowId));
        }
    }

    // --- Battle NPCs (BNpcBase + BNpcCustomize + fetched name map) ------------
    private void BuildBattleNpcs(List<NpcEntry> result,
        Lumina.Excel.ExcelSheet<Race>? raceSheet, Lumina.Excel.ExcelSheet<Tribe>? tribeSheet)
    {
        var bNpcBase = _data.GetExcelSheet<BNpcBase>();
        var bNpcName = _data.GetExcelSheet<BNpcName>();
        if (bNpcBase is null || bNpcName is null)
        {
            _log.Error("Could not load BNpcBase / BNpcName sheets.");
            return;
        }

        foreach (var row in bNpcBase)
        {
            if (row.ModelChara.ValueNullable is not { } mc || mc.Type != 1)
                continue;

            // Names for this base come from the fetched mapping (not a sheet link).
            var nameIds = _bnpcNames.NamesFor(row.RowId);
            if (nameIds.Count == 0)
                continue;

            if (row.BNpcCustomize.ValueNullable is not { } bc)
                continue;
            var customize = ReadCustomizeFromBNpc(bc);
            var isFemale  = customize[1] == 1;

            var pieces = row.NpcEquip.ValueNullable is { } equip
                ? ReadEquipFromNpcEquip(equip)
                : new List<NpcPiece>();

            // One entry per associated name (that's why some NPCs appear multiple
            // times under different names, e.g. the Brave units).
            foreach (var nameId in nameIds)
            {
                var nameRow = bNpcName.GetRowOrDefault(nameId);
                var name = nameRow?.Singular.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                result.Add(MakeEntry(row.RowId, name, customize, isFemale, pieces, raceSheet, tribeSheet,
                    customize[0], customize[4])); // Race index 0, Tribe/Clan index 4
            }
        }
    }

    private static NpcEntry MakeEntry(uint rowId, string name, byte[] customize, bool isFemale,
        List<NpcPiece> pieces, Lumina.Excel.ExcelSheet<Race>? raceSheet, Lumina.Excel.ExcelSheet<Tribe>? tribeSheet,
        uint raceId, uint tribeId)
    {
        var raceName = ResolveRace(raceSheet, raceId, isFemale);
        var clanName = ResolveTribe(tribeSheet, tribeId, isFemale);

        // Search haystack: name + race + clan + the NPC id (so an exact id search
        // pinpoints a specific entry among same-named NPCs).
        var sb = new StringBuilder(name).Append('\n').Append(raceName).Append('\n')
            .Append(clanName).Append('\n').Append(rowId);
        var search = sb.ToString().ToLowerInvariant();

        return new NpcEntry(rowId, name, raceName, clanName, isFemale, pieces, customize, search);
    }

    private static string ResolveRace(Lumina.Excel.ExcelSheet<Race>? sheet, uint rowId, bool female)
    {
        if (sheet is null || rowId == 0 || sheet.GetRowOrDefault(rowId) is not { } r)
            return string.Empty;
        return (female ? r.Feminine : r.Masculine).ToString();
    }

    private static string ResolveTribe(Lumina.Excel.ExcelSheet<Tribe>? sheet, uint rowId, bool female)
    {
        if (sheet is null || rowId == 0 || sheet.GetRowOrDefault(rowId) is not { } t)
            return string.Empty;
        return (female ? t.Feminine : t.Masculine).ToString();
    }

    // --- Customize readers (26-byte array, indices verbatim from Glamourer) ---
    private static byte[] ReadCustomizeFromENpc(ENpcBase r)
    {
        var c = new byte[26];
        c[0]  = (byte)r.Race.RowId;
        c[1]  = (byte)r.Gender;
        c[2]  = (byte)r.BodyType;
        c[3]  = (byte)r.Height;
        c[4]  = (byte)r.Tribe.RowId;
        c[5]  = (byte)r.Face;
        c[6]  = (byte)r.HairStyle;
        c[7]  = (byte)r.HairHighlight;
        c[8]  = (byte)r.SkinColor;
        c[9]  = (byte)r.EyeHeterochromia;
        c[10] = (byte)r.HairColor;
        c[11] = (byte)r.HairHighlightColor;
        c[12] = (byte)r.FacialFeature;
        c[13] = (byte)r.FacialFeatureColor;
        c[14] = (byte)r.Eyebrows;
        c[15] = (byte)r.EyeColor;
        c[16] = (byte)r.EyeShape;
        c[17] = (byte)r.Nose;
        c[18] = (byte)r.Jaw;
        c[19] = (byte)r.Mouth;
        c[20] = (byte)r.LipColor;
        c[21] = (byte)r.BustOrTone1;
        c[22] = (byte)r.ExtraFeature1;
        c[23] = (byte)r.ExtraFeature2OrBust;
        c[24] = (byte)r.FacePaint;
        c[25] = (byte)r.FacePaintColor;
        return c;
    }

    private static byte[] ReadCustomizeFromBNpc(BNpcCustomize r)
    {
        var c = new byte[26];
        c[0]  = (byte)r.Race.RowId;
        c[1]  = (byte)r.Gender;
        c[2]  = (byte)r.BodyType;
        c[3]  = (byte)r.Height;
        c[4]  = (byte)r.Tribe.RowId;
        c[5]  = (byte)r.Face;
        c[6]  = (byte)r.HairStyle;
        c[7]  = (byte)r.HairHighlight;
        c[8]  = (byte)r.SkinColor;
        c[9]  = (byte)r.EyeHeterochromia;
        c[10] = (byte)r.HairColor;
        c[11] = (byte)r.HairHighlightColor;
        c[12] = (byte)r.FacialFeature;
        c[13] = (byte)r.FacialFeatureColor;
        c[14] = (byte)r.Eyebrows;
        c[15] = (byte)r.EyeColor;
        c[16] = (byte)r.EyeShape;
        c[17] = (byte)r.Nose;
        c[18] = (byte)r.Jaw;
        c[19] = (byte)r.Mouth;
        c[20] = (byte)r.LipColor;
        c[21] = (byte)r.BustOrTone1;
        c[22] = (byte)r.ExtraFeature1;
        c[23] = (byte)r.ExtraFeature2OrBust;
        c[24] = (byte)r.FacePaint;
        c[25] = (byte)r.FacePaintColor;
        return c;
    }

    // --- Equipment readers ---------------------------------------------------
    private static List<NpcPiece> ReadEquipFromENpc(ENpcBase r)
    {
        var p = new List<NpcPiece>();
        AddEquip(p, ApiEquipSlot.Head,    r.ModelHead,      r.DyeHead.RowId,      r.Dye2Head.RowId);
        AddEquip(p, ApiEquipSlot.Body,    r.ModelBody,      r.DyeBody.RowId,      r.Dye2Body.RowId);
        AddEquip(p, ApiEquipSlot.Hands,   r.ModelHands,     r.DyeHands.RowId,     r.Dye2Hands.RowId);
        AddEquip(p, ApiEquipSlot.Legs,    r.ModelLegs,      r.DyeLegs.RowId,      r.Dye2Legs.RowId);
        AddEquip(p, ApiEquipSlot.Feet,    r.ModelFeet,      r.DyeFeet.RowId,      r.Dye2Feet.RowId);
        AddEquip(p, ApiEquipSlot.Ears,    r.ModelEars,      r.DyeEars.RowId,      r.Dye2Ears.RowId);
        AddEquip(p, ApiEquipSlot.Neck,    r.ModelNeck,      r.DyeNeck.RowId,      r.Dye2Neck.RowId);
        AddEquip(p, ApiEquipSlot.Wrists,  r.ModelWrists,    r.DyeWrists.RowId,    r.Dye2Wrists.RowId);
        AddEquip(p, ApiEquipSlot.RFinger, r.ModelRightRing, r.DyeRightRing.RowId, r.Dye2RightRing.RowId);
        AddEquip(p, ApiEquipSlot.LFinger, r.ModelLeftRing,  r.DyeLeftRing.RowId,  r.Dye2LeftRing.RowId);
        return p;
    }

    private static List<NpcPiece> ReadEquipFromNpcEquip(NpcEquip r)
    {
        var p = new List<NpcPiece>();
        AddEquip(p, ApiEquipSlot.Head,    r.ModelHead,      r.DyeHead.RowId,      r.Dye2Head.RowId);
        AddEquip(p, ApiEquipSlot.Body,    r.ModelBody,      r.DyeBody.RowId,      r.Dye2Body.RowId);
        AddEquip(p, ApiEquipSlot.Hands,   r.ModelHands,     r.DyeHands.RowId,     r.Dye2Hands.RowId);
        AddEquip(p, ApiEquipSlot.Legs,    r.ModelLegs,      r.DyeLegs.RowId,      r.Dye2Legs.RowId);
        AddEquip(p, ApiEquipSlot.Feet,    r.ModelFeet,      r.DyeFeet.RowId,      r.Dye2Feet.RowId);
        AddEquip(p, ApiEquipSlot.Ears,    r.ModelEars,      r.DyeEars.RowId,      r.Dye2Ears.RowId);
        AddEquip(p, ApiEquipSlot.Neck,    r.ModelNeck,      r.DyeNeck.RowId,      r.Dye2Neck.RowId);
        AddEquip(p, ApiEquipSlot.Wrists,  r.ModelWrists,    r.DyeWrists.RowId,    r.Dye2Wrists.RowId);
        AddEquip(p, ApiEquipSlot.RFinger, r.ModelRightRing, r.DyeRightRing.RowId, r.Dye2RightRing.RowId);
        AddEquip(p, ApiEquipSlot.LFinger, r.ModelLeftRing,  r.DyeLeftRing.RowId,  r.Dye2LeftRing.RowId);
        return p;
    }

    private static void AddEquip(List<NpcPiece> pieces, ApiEquipSlot slot, uint modelValue, uint dye, uint dye2)
    {
        if (modelValue == 0)
            return; // empty slot ("Nothing")
        var model   = (ushort)(modelValue & 0xFFFF);
        var variant = (byte)((modelValue >> 16) & 0xFF);
        pieces.Add(new NpcPiece(slot, model, variant, (byte)dye, (byte)dye2, $"{model}-{variant}"));
    }

    public static bool IsAccessory(ApiEquipSlot slot) => OutfitService.IsAccessory(slot);
}
