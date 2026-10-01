using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;

namespace HOutfits;

/// <summary>
/// Everything the gear scan produces, published once (immutable) so the UI thread can read it without locking.
/// </summary>
public sealed class GearData
{
    /// <summary>Sets recovered from item names, already shaped as <see cref="OutfitSet"/> so the Outfit sets tab lists them alongside the game's own.</summary>
    public IReadOnlyList<OutfitSet> GroupedSets { get; init; } = Array.Empty<OutfitSet>();

    /// <summary>Pieces that belong to no set at all, per <see cref="GearSlot"/>, one tile per distinct look.</summary>
    public IReadOnlyList<GearLook>[] Looks { get; init; } = Array.Empty<IReadOnlyList<GearLook>>();

    /// <summary>The same loose pieces, one tile per item (when duplicate looks are not collapsed).</summary>
    public IReadOnlyList<GearLook>[] AllLooks { get; init; } = Array.Empty<IReadOnlyList<GearLook>>();

    public int LoosePieceCount { get; init; }

    /// <summary>
    /// True when the game client is not English, i.e. item names differ from the English ones the grouping rules read. The
    /// labels of the name-grouped sets are English-derived, so the Outfit sets tab lists them after the game's own sets there
    /// rather than interleaving English labels among localized names.
    /// </summary>
    public bool LocalizedClient { get; init; }
}

/// <summary>
/// Scans the Item sheet for wearable gear that the Outfit sets tab does not already cover (the game's own
/// <c>MirageStoreSetItem</c> sets), then splits it in two: pieces that clearly belong together are grouped into sets by
/// <see cref="GearGrouper"/>; everything else stays loose. Runs once, lazily, on a background thread, so opening the window
/// is the only thing that ever triggers the work.
/// </summary>
public sealed class GearService : IDisposable
{
    /// <summary>Synthetic row ids for name-grouped sets (real <c>MirageStoreSetItem</c> row ids are far below this).</summary>
    private const uint GroupedRowIdBase = 0x80000000u;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    private int _started;
    private volatile bool _disposed;
    private volatile GearData? _result;
    private volatile string? _error;

    public GearService(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>Null until the background scan has finished.</summary>
    public GearData? Data => _result;

    public string? Error => _error;

    public bool Building => Volatile.Read(ref _started) == 1 && _result is null && _error is null;

    /// <summary>Tells an in-flight scan to stop at its next checkpoint (the plugin is unloading).</summary>
    public void Dispose() => _disposed = true;

    /// <summary>Kicks the scan off once; later calls are a cheap no-op.</summary>
    public void EnsureBuilt()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var t = new Thread(Build)
        {
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal,
            Name         = "HOutfits-Gear",
        };
        t.Start();
    }

    private void Build()
    {
        try
        {
            var items   = _data.GetExcelSheet<Item>();
            var sets    = _data.GetExcelSheet<MirageStoreSetItem>();
            // The grouping rules read English names (looked up by item id), so they find the same sets on every client
            // language. The client-language sheet above is still what the UI shows.
            var itemsEn = TryEnglishSheet<Item>();
            if (items is null || sets is null)
            {
                _error = "Couldn't load the Item / MirageStoreSetItem sheets.";
                _log.Error(_error);
                return;
            }

            // Items the Outfit sets tab already lists (weapons included, though the tab skips them).
            var inSet = new HashSet<uint>();
            foreach (var r in sets)
            {
                if (r.RowId == 0) continue;
                foreach (var id in new[]
                         {
                             r.MainHand.RowId, r.OffHand.RowId, r.Head.RowId, r.Body.RowId, r.Hands.RowId, r.Legs.RowId,
                             r.Feet.RowId, r.Earrings.RowId, r.Necklace.RowId, r.Bracelets.RowId, r.Ring.RowId,
                         })
                    if (id != 0) inSet.Add(id);
            }

            var loose = new List<GearItem>(16000);
            foreach (var it in items)
            {
                if (_disposed) return;
                if (it.RowId == 0 || it.ModelMain == 0 || inSet.Contains(it.RowId)) continue;
                if (it.EquipSlotCategory.RowId == 0 || !it.EquipSlotCategory.IsValid) continue;
                var slot = SlotOf(it.EquipSlotCategory.Value);
                if (slot is null) continue;
                var name = it.Name.ToString().Trim();
                if (name.Length == 0) continue;
                var job = it.ClassJobCategory.ValueNullable?.Name.ToString() ?? string.Empty;
                string? english = null;
                if (itemsEn is not null)
                {
                    try { english = EnglishNameOf(itemsEn.GetRowOrDefault(it.RowId)?.Name, name); }
                    catch (Exception ex)
                    {
                        // Switch the English lookup off rather than failing the whole scan: grouping then reads the game's own
                        // language, exactly as it did before English names were used.
                        _log.Warning(ex, "English item names are unavailable; grouping will use the game's language.");
                        itemsEn = null;
                    }
                }
                loose.Add(new GearItem(it.RowId, name, it.Icon, slot.Value, it.LevelEquip, it.ModelMain, job, english));
            }

            if (_disposed) return;

            var grouped   = GearGrouper.BuildSets(loose);
            var groupedId = new HashSet<uint>(grouped.SelectMany(g => g.Items.Select(i => i.ItemId)));
            var singles   = loose.Where(i => !groupedId.Contains(i.ItemId)).ToList();

            // Facewear never belongs to a set, so every entry is a loose tile.
            var facewear = ReadFacewear();
            var tiles    = singles.Concat(facewear).ToList();

            var asOutfits = grouped.Select(g => new OutfitSet(
                GroupedRowIdBase + (uint)g.Id,
                g.Name,
                g.Icon,
                g.Items.Select(i => new OutfitPiece(i.ItemId, i.Name, i.Icon, ToApi(i.Slot))).ToList(),
                g.SearchText,
                Grouped: true)).ToList();

            _result = new GearData
            {
                GroupedSets     = asOutfits,
                Looks           = GearGrouper.BuildLooks(tiles, dedupe: true).Select(l => (IReadOnlyList<GearLook>)l).ToArray(),
                AllLooks        = GearGrouper.BuildLooks(tiles, dedupe: false).Select(l => (IReadOnlyList<GearLook>)l).ToArray(),
                LoosePieceCount = tiles.Count,
                LocalizedClient = loose.Any(i => i.EnglishName is not null),
            };

            _log.Information(
                "Gear scan: {Loose} loose wearable items -> {Sets} name-grouped sets ({Grouped} pieces) + {Singles} ungrouped pieces + {Face} facewear.",
                loose.Count, asOutfits.Count, groupedId.Count, singles.Count, facewear.Count);
        }
        catch (Exception ex)
        {
            _error = "The gear scan failed (see /xllog).";
            _log.Error(ex, "Gear scan failed.");
        }
    }

    /// <summary>
    /// Facewear is its own slot and is not in the Item sheet: the game keeps it in the Glasses sheet, one row per shape and
    /// colour (about 730 of them; a dozen blank placeholder rows are skipped). The row id is the id Glamourer knows a
    /// facewear piece by, and the row carries its own icon. Rows come back in sheet order, which keeps each shape's colour
    /// variants together.
    /// </summary>
    private List<GearItem> ReadFacewear()
    {
        var result = new List<GearItem>(800);
        var glasses   = _data.GetExcelSheet<Glasses>();
        var glassesEn = TryEnglishSheet<Glasses>();
        if (glasses is null)
        {
            _log.Warning("Couldn't load the Glasses sheet; the Facewear chip will be empty.");
            return result;
        }

        foreach (var g in glasses)
        {
            if (g.RowId == 0 || g.Icon <= 0) continue;
            var name = g.Name.ToString().Trim();
            if (name.Length == 0) continue;
            string? english = null;
            if (glassesEn is not null)
            {
                try { english = EnglishNameOf(glassesEn.GetRowOrDefault(g.RowId)?.Name, name); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "English facewear names are unavailable; search will use the game's language only.");
                    glassesEn = null;
                }
            }
            result.Add(new GearItem(g.RowId, name, (uint)g.Icon, GearSlot.Facewear, 0, g.Model, string.Empty, english));
        }
        return result;
    }

    /// <summary>The English variant of a sheet, or null if it can't be had (never throws).</summary>
    private Lumina.Excel.ExcelSheet<T>? TryEnglishSheet<T>() where T : struct, Lumina.Excel.IExcelRow<T>
    {
        try { return _data.GetExcelSheet<T>(ClientLanguage.English); }
        catch (Exception ex)
        {
            _log.Warning(ex, "The English {Sheet} sheet is unavailable.", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// The English name to remember alongside <paramref name="shown"/>, or null when there is nothing to add (an English
    /// client, where the two are the same, or the English sheet is unavailable).
    /// </summary>
    private static string? EnglishNameOf(Lumina.Text.ReadOnly.ReadOnlySeString? english, string shown)
    {
        if (english is not { } name) return null;
        var en = name.ToString().Trim();
        return en.Length == 0 || string.Equals(en, shown, StringComparison.Ordinal) ? null : en;
    }

    /// <summary>Apply one loose piece to the actor. Only touches that slot, so it is additive over whatever else is worn.</summary>
    public bool ApplyPiece(GearItem item, GlamourerIpc glam, int objectIndex)
    {
        // Facewear goes through Glamourer's separate "bonus item" call; everything else is an ordinary equipment item.
        var ec = item.Slot == GearSlot.Facewear
            ? glam.ApplyBonusItem(objectIndex, ApiBonusSlot.Glasses, item.ItemId)
            : glam.ApplyItem(objectIndex, ToApi(item.Slot), item.ItemId);
        if (ec == GlamourerApiEc.Success)
            return true;

        _log.Warning("Applying {Item} ({Slot}) failed: {Ec}", item.Name, item.Slot, ec);
        return false;
    }

    /// <summary>Rings go to the right finger, matching how the Outfit sets tab applies them. Facewear has no equipment slot.</summary>
    internal static ApiEquipSlot ToApi(GearSlot slot) => slot switch
    {
        GearSlot.Head   => ApiEquipSlot.Head,
        GearSlot.Body   => ApiEquipSlot.Body,
        GearSlot.Hands  => ApiEquipSlot.Hands,
        GearSlot.Legs   => ApiEquipSlot.Legs,
        GearSlot.Feet   => ApiEquipSlot.Feet,
        GearSlot.Ears   => ApiEquipSlot.Ears,
        GearSlot.Neck   => ApiEquipSlot.Neck,
        GearSlot.Wrists => ApiEquipSlot.Wrists,
        GearSlot.Ring   => ApiEquipSlot.RFinger,
        _               => ApiEquipSlot.Unknown,
    };

    /// <summary>
    /// The slot an item occupies = the one column of its EquipSlotCategory set to 1 (-1 means "blocks that slot", 0 means
    /// unrelated). Weapons and soul crystals return null: this tab is armour and accessories only.
    /// </summary>
    private static GearSlot? SlotOf(EquipSlotCategory c)
    {
        if (c.Head == 1) return GearSlot.Head;
        if (c.Body == 1) return GearSlot.Body;
        if (c.Gloves == 1) return GearSlot.Hands;
        if (c.Legs == 1) return GearSlot.Legs;
        if (c.Feet == 1) return GearSlot.Feet;
        if (c.Ears == 1) return GearSlot.Ears;
        if (c.Neck == 1) return GearSlot.Neck;
        if (c.Wrists == 1) return GearSlot.Wrists;
        if (c.FingerR == 1 || c.FingerL == 1) return GearSlot.Ring;
        return null;
    }
}
