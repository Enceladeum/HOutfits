using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;

namespace HOutfits;

/// <summary>
/// The game's equipment parameter (EQP) table: one 64-bit entry per gear model saying how it affects the other slots,
/// for instance a body piece that hides the legs and hands. Bit layout from Penumbra's EqpEntry. The file holds 64 blocks
/// of 160 entries; its first entry is a mask of which blocks are present, and only those are stored, in order.
/// </summary>
public sealed class EqpTable
{
    private const ulong BodyShowLeg  = 0x01_00ul;
    private const ulong BodyShowHand = 0x02_00ul;
    private const ulong BodyEnabled  = 0x00_01ul;

    private readonly byte[] _data;

    private EqpTable(byte[] data) => _data = data;

    /// <summary>Reads the table, or returns null when it can't be read (the tab then just doesn't say what a piece covers).</summary>
    public static EqpTable? Load(IDataManager data, IPluginLog log)
    {
        try
        {
            var file = data.GetFile("chara/xls/equipmentparameter/equipmentparameter.eqp");
            return file is { Data.Length: >= 8 } ? new EqpTable(file.Data) : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Couldn't read the EQP table.");
            return null;
        }
    }

    public ulong Entry(int model)
    {
        var block = model / 160;
        if (block >= 64)
            return 0;
        var control = BitConverter.ToUInt64(_data, 0);
        var bit     = 1ul << block;
        if ((control & bit) == 0)
            return 0;
        var index = BitOperations.PopCount(control & (bit - 1)) * 160 + model % 160;
        return (index + 1) * 8 <= _data.Length ? BitConverter.ToUInt64(_data, index * 8) : 0;
    }

    /// <summary>"legs, hands" for a body piece that hides those slots, empty when it hides neither.</summary>
    public string BodyCovers(int model)
    {
        var e = Entry(model);
        if ((e & BodyEnabled) == 0)
            return string.Empty;
        var parts = new List<string>(2);
        if ((e & BodyShowLeg) == 0)
            parts.Add("legs");
        if ((e & BodyShowHand) == 0)
            parts.Add("hands");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Keys an NPC piece's look the way an item's ModelMain does, so NPC gear can be matched to items and to other NPCs' gear.
/// Both rings are one ring model, so a left ring is keyed as a right one.
/// </summary>
public static class NpcLook
{
    public static (ApiEquipSlot Slot, ulong Model) Key(NpcPiece piece)
        => (piece.Slot == ApiEquipSlot.LFinger ? ApiEquipSlot.RFinger : piece.Slot,
            NpcStateBuilder.IsWeapon(piece.Slot)
                ? piece.Model | ((ulong)piece.Secondary << 16) | ((ulong)piece.Variant << 32)
                : piece.Model | ((ulong)piece.Variant << 16));

    /// <summary>For every look NPCs wear, the distinct names of the NPCs that wear it.</summary>
    public static Dictionary<(ApiEquipSlot, ulong), SortedSet<string>> Wearers(IReadOnlyList<NpcEntry> npcs)
    {
        var result = new Dictionary<(ApiEquipSlot, ulong), SortedSet<string>>();
        foreach (var npc in npcs)
            foreach (var piece in npc.Pieces)
            {
                if (piece.Model == 0)
                    continue;
                var key = Key(piece);
                if (!result.TryGetValue(key, out var names))
                    result[key] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(npc.Name);
            }
        return result;
    }
}

/// <summary>
/// The item behind each look, where there is one: an NPC piece with the same model and variant as an item looks exactly like
/// it, so the NPC tab can show that item's icon and name instead of a blank slot. The lowest item id wins; a main hand's
/// second model (the other dagger, the arrows) also counts for the off hand.
/// </summary>
public sealed class ModelItems
{
    public static readonly ModelItems Empty = new(new Dictionary<(ApiEquipSlot, ulong), GearItem>());

    private readonly IReadOnlyDictionary<(ApiEquipSlot, ulong), GearItem> _items;

    private ModelItems(IReadOnlyDictionary<(ApiEquipSlot, ulong), GearItem> items) => _items = items;

    public GearItem? For(NpcPiece piece) => _items.TryGetValue(NpcLook.Key(piece), out var item) ? item : null;

    public static ModelItems Build(IEnumerable<GearItem> items)
    {
        var result = new Dictionary<(ApiEquipSlot, ulong), GearItem>();
        foreach (var item in items.OrderBy(i => i.ItemId))
        {
            if (item.Slot == GearSlot.Facewear)
                continue;
            var slot = GearService.ToApi(item.Slot);
            var mask = item.IsWeapon ? 0xFFFF_FFFF_FFFFul : 0xFFFF_FFFFul;
            result.TryAdd((slot, item.Model & mask), item);
            if (item.Slot == GearSlot.MainHand && item.ModelSub != 0)
                result.TryAdd((ApiEquipSlot.OffHand, item.ModelSub & mask), item);
        }
        return new ModelItems(result);
    }
}
