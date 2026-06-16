using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;

namespace HOutfits;

/// <summary>
/// One displayable outfit row: the set's name + icon, and the component pieces,
/// each already paired with its equip slot.
/// </summary>
public sealed record OutfitSet(
    uint RowId,
    string Name,
    uint Icon,
    IReadOnlyList<OutfitPiece> Pieces,
    string SearchText);

public sealed record OutfitPiece(uint ItemId, string Name, uint Icon, ApiEquipSlot Slot);

/// <summary>
/// Builds the outfit list from the MirageStoreSetItem sheet and applies a set
/// (or a single piece) to an actor through Glamourer.
///
/// Schema notes (verified against EXDSchema "latest"):
///
/// - MirageStoreSetItem has NO name/category columns — only eleven per-slot
///   link columns, each a RowRef&lt;Item&gt; for that exact slot:
///     MainHand, OffHand, Head, Body, Hands, Legs, Feet,
///     Earrings, Necklace, Bracelets, Ring
///   So we map column -> ApiEquipSlot directly; no EquipSlotCategory lookup.
///
/// - The SET NAME is the name of the Item whose id == the set row's own RowId
///   (a MirageStoreSetItem row at id N corresponds to Item N, whose name is the
///   set name, e.g. "Imperial Attire of Fending"). This is how HaselDebug's
///   SetColumn derives it (GetItemName(row.RowId)). We name/icon the row from
///   that item, falling back to the chest piece only if it doesn't resolve.
///
/// Weapons (MainHand/OffHand) are skipped so an outfit swap never changes your
/// weapon; flip _includeWeapons to include them.
/// </summary>
public sealed class OutfitService
{
    private readonly bool _includeWeapons = false;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    private List<OutfitSet>? _cache;

    public OutfitService(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    public IReadOnlyList<OutfitSet> Sets => _cache ??= BuildSets();

    private List<OutfitSet> BuildSets()
    {
        var result = new List<OutfitSet>();

        var setSheet  = _data.GetExcelSheet<MirageStoreSetItem>();
        var itemSheet = _data.GetExcelSheet<Item>();
        if (setSheet is null || itemSheet is null)
        {
            _log.Error("Could not load the MirageStoreSetItem / Item sheets.");
            return result;
        }

        foreach (var row in setSheet)
        {
            if (row.RowId == 0)
                continue;

            var pieces = new List<OutfitPiece>();

            if (_includeWeapons)
            {
                AddPiece(pieces, row.MainHand, ApiEquipSlot.MainHand);
                AddPiece(pieces, row.OffHand,  ApiEquipSlot.OffHand);
            }
            AddPiece(pieces, row.Head,      ApiEquipSlot.Head);
            AddPiece(pieces, row.Body,      ApiEquipSlot.Body);
            AddPiece(pieces, row.Hands,     ApiEquipSlot.Hands);
            AddPiece(pieces, row.Legs,      ApiEquipSlot.Legs);
            AddPiece(pieces, row.Feet,      ApiEquipSlot.Feet);
            AddPiece(pieces, row.Earrings,  ApiEquipSlot.Ears);
            AddPiece(pieces, row.Necklace,  ApiEquipSlot.Neck);
            AddPiece(pieces, row.Bracelets, ApiEquipSlot.Wrists);
            AddPiece(pieces, row.Ring,      ApiEquipSlot.RFinger);

            if (pieces.Count == 0)
                continue;

            // Set name/icon = the Item at this row's own id. Fall back to the
            // chest (or first) piece only if that item doesn't resolve.
            string name;
            uint icon;
            var selfItem = itemSheet.GetRowOrDefault(row.RowId);
            if (selfItem is { } si && si.RowId != 0 && si.Name.ToString().Length > 0)
            {
                name = si.Name.ToString();
                icon = si.Icon;
            }
            else
            {
                var rep = pieces.Find(p => p.Slot == ApiEquipSlot.Body) ?? pieces[0];
                name = rep.Name;
                icon = rep.Icon;
            }

            // Search haystack: set name + every piece name, lower-cased once so
            // the filter is a cheap Contains. Lets "ushanka" find the Imperial
            // sets that include the Ushanka head piece.
            var sb = new StringBuilder(name);
            foreach (var p in pieces)
                sb.Append('\n').Append(p.Name);
            var search = sb.ToString().ToLowerInvariant();

            result.Add(new OutfitSet(row.RowId, name, icon, pieces, search));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _log.Information("Loaded {Count} outfit sets.", result.Count);
        return result;
    }

    private static void AddPiece(List<OutfitPiece> pieces, Lumina.Excel.RowRef<Item> itemRef, ApiEquipSlot slot)
    {
        if (itemRef.RowId == 0 || !itemRef.IsValid)
            return;

        var item = itemRef.Value;
        if (item.RowId == 0)
            return;

        pieces.Add(new OutfitPiece(item.RowId, item.Name.ToString(), item.Icon, slot));
    }

    /// <summary>
    /// Apply a whole set to the actor at <paramref name="objectIndex"/>.
    /// Returns (applied, failed) counts.
    /// </summary>
    public (int applied, int failed) Apply(OutfitSet set, GlamourerIpc glam, int objectIndex)
    {
        var applied = 0;
        var failed  = 0;
        foreach (var piece in set.Pieces)
        {
            if (ApplyPiece(piece, glam, objectIndex))
                applied++;
            else
                failed++;
        }
        return (applied, failed);
    }

    /// <summary>
    /// Apply a single piece to the actor. Only touches that one slot, so it's
    /// additive over whatever else is worn ("just the gloves"). Returns success.
    /// </summary>
    public bool ApplyPiece(OutfitPiece piece, GlamourerIpc glam, int objectIndex)
    {
        var ec = glam.ApplyItem(objectIndex, piece.Slot, piece.ItemId);
        if (ec == GlamourerApiEc.Success)
            return true;

        _log.Warning("SetItem failed for {Item} ({Slot}): {Ec}", piece.Name, piece.Slot, ec);
        return false;
    }
}
