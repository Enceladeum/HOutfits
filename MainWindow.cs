using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Glamourer.Api.Enums;

namespace HOutfits;

/// <summary>
/// The plugin window. Three tabs:
///  - Outfit sets: the game's named sets, plus (optionally) sets recovered from
///    item names, applied via Glamourer SetItem.
///  - Loose gear: wearable pieces that belong to no set at all, browsed by slot
///    as an icon grid with a large preview on hover, applied via Glamourer SetItem.
///  - NPCs: named human NPCs, applied via Glamourer ApplyState (their gear has
///    no item id, so it's whole-state application, not SetItem).
///
/// The set-based tabs carry their own "Include accessories" toggle (bound to the
/// same setting, shown in-context beside each tab's other options) and all tabs
/// share the "Revert changes" button in the header. Draw callbacks stay
/// side-effect-light: clicks queue work that fires at the top of Draw next frame.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float IconSize = 32f;

    private readonly OutfitService _outfits;
    private readonly GearService _gear;
    private readonly NpcService _npcs;
    private readonly NpcStateBuilder _npcState;
    private readonly GlamourerIpc _glam;
    private readonly MonikerIpc _moniker;
    private readonly ITextureProvider _textures;
    private readonly SlotIconService _slotIcons;
    private readonly IPluginLog _log;
    private readonly Configuration _config;

    // Sets tab state
    private string _setFilter = string.Empty;
    private OutfitSet? _pendingSet;
    private OutfitPiece? _pendingPiece;

    // The game's sets merged with the name-grouped ones, rebuilt only when either list (or the toggle) changes.
    private IReadOnlyList<OutfitSet>? _mergedSets;
    private IReadOnlyList<OutfitSet>? _mergedFromOfficial;
    private IReadOnlyList<OutfitSet>? _mergedFromGrouped;

    // Loose gear tab state
    private string _looseFilter = string.Empty;
    private GearItem? _pendingLoose;

    // NPC tab state
    private string _npcFilter = string.Empty;
    private NpcEntry? _pendingNpc;
    private (NpcEntry npc, NpcPiece piece)? _pendingNpcPiece;

    // Shared
    private bool _pendingRevert;
    private bool _appliedNameThisSession;
    private string _status = string.Empty;
    private Vector4 _statusColor = new(0.7f, 0.7f, 0.7f, 1f);

    public MainWindow(OutfitService outfits, GearService gear, NpcService npcs, NpcStateBuilder npcState,
        GlamourerIpc glam, MonikerIpc moniker, ITextureProvider textures, SlotIconService slotIcons,
        IPluginLog log, Configuration config)
        : base("HOutfits###HOutfitsMain")
    {
        _outfits   = outfits;
        _gear      = gear;
        _npcs      = npcs;
        _npcState  = npcState;
        _glam      = glam;
        _moniker   = moniker;
        _textures  = textures;
        _slotIcons = slotIcons;
        _log       = log;
        _config    = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(1400, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrainPendingActions();

        if (!_glam.Available)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f),
                "Glamourer isn't loaded (or is too old). Install/update it to apply outfits.");
            return;
        }

        // The first time the window draws, start the one-off background scan for name-grouped sets and loose gear.
        _gear.EnsureBuilt();

        DrawSharedHeader();

        if (!ImGui.BeginTabBar("###hotabs"))
            return;

        if (ImGui.BeginTabItem("Outfit sets"))
        {
            DrawSetsTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Loose gear"))
        {
            DrawLooseTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("NPCs"))
        {
            DrawNpcTab();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrainPendingActions()
    {
        if (_pendingSet is { } ps)          { _pendingSet = null;       DoApplySet(ps); }
        if (_pendingPiece is { } pp)        { _pendingPiece = null;     DoApplyPiece(pp); }
        if (_pendingLoose is { } pl)        { _pendingLoose = null;     DoApplyLoose(pl); }
        if (_pendingNpc is { } pn)          { _pendingNpc = null;       DoApplyNpc(pn); }
        if (_pendingNpcPiece is { } pnp)    { _pendingNpcPiece = null;  DoApplyNpcPiece(pnp.npc, pnp.piece); }
        if (_pendingRevert)                 { _pendingRevert = false;   DoRevert(); }
    }

    private void DrawSharedHeader()
    {
        if (ImGui.Button("Revert changes"))
            _pendingRevert = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Returns to game state (equipment, appearance, and any applied name).");

        // The status rides on the button's row rather than getting a line of its own, so the header is always one line
        // tall: the tabs, and the search box under them, never shift when the first message appears.
        if (_status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(_statusColor, _status);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_status);   // the full message, in case a long item name clips at the window edge
        }

        ImGui.Separator();
    }

    /// <summary>
    /// The "Include accessories" toggle, drawn in-context by each tab. Both tabs
    /// bind the same <see cref="Configuration.IncludeAccessories"/> setting, so
    /// flipping it on one tab is reflected on the other. The caller decides
    /// layout (e.g. calls <c>ImGui.SameLine()</c> first).
    /// </summary>
    private void DrawIncludeAccessoriesCheckbox()
    {
        var includeAccessories = _config.IncludeAccessories;
        if (ImGui.Checkbox("Include accessories", ref includeAccessories))
        {
            _config.IncludeAccessories = includeAccessories;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When off, applying a whole set or NPC skips earrings, necklace, bracelets, and rings.\n" +
                "Click an individual accessory to apply just that piece regardless.");
    }

    /// <summary>
    /// The game's own sets plus, when enabled and ready, the sets recovered by name. Merged once and cached until either
    /// list (or the toggle) changes; sorted with the same comparer the game's list already uses so its order is unchanged.
    /// </summary>
    private IReadOnlyList<OutfitSet> AllSets()
    {
        var official = _outfits.Sets;
        var grouped  = _config.IncludeGroupedSets ? _gear.Data?.GroupedSets : null;
        if (grouped is null || grouped.Count == 0)
            return official;

        if (!ReferenceEquals(_mergedFromOfficial, official) || !ReferenceEquals(_mergedFromGrouped, grouped))
        {
            // On an English client everything sorts into one alphabetical list. On other languages the recovered sets carry
            // English labels, which would sort oddly among localized names (all ahead of Japanese ones, say), so they follow
            // the game's own sets instead, each block still alphabetical.
            _mergedSets = _gear.Data?.LocalizedClient == true
                ? official.Concat(grouped).ToList()
                : official.Concat(grouped).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            _mergedFromOfficial = official;
            _mergedFromGrouped  = grouped;
        }
        return _mergedSets!;
    }

    /// <summary>
    /// A search field with a small [x] at its right edge that clears it. The button stays in place (dimmed) while the
    /// field is empty, so the layout never jumps as you type.
    /// </summary>
    private static void DrawFilterBox(string id, string hint, ref string text)
    {
        var button = ImGui.GetFrameHeight();                       // a square, as tall as the field
        ImGui.SetNextItemWidth(-(button + ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint(id, hint, ref text, 128);

        ImGui.SameLine();
        var empty = text.Length == 0;
        if (empty)
            ImGui.BeginDisabled();
        if (ImGui.Button($"x###{id.TrimStart('#')}_clear", new Vector2(button, button)))
            text = string.Empty;
        if (empty)
            ImGui.EndDisabled();
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear the search");
    }

    private void DrawIncludeGroupedSetsCheckbox()
    {
        var includeGrouped = _config.IncludeGroupedSets;
        if (ImGui.Checkbox("Include name-grouped sets", ref includeGrouped))
        {
            _config.IncludeGroupedSets = includeGrouped;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Also list sets the game doesn't name, found by matching item names\n" +
                "(e.g. \"Allagan of Striking\"). Turn off to see only the game's own sets.\n" +
                "They're grouped by name, so the odd one can be imperfect.");
    }

    private void DrawSetsTab()
    {
        // The search box is the first row of every tab, so it sits at the same height on all of them.
        DrawFilterBox("###setfilter", "Filter by set or item name (e.g. \"ushanka\")", ref _setFilter);

        DrawIncludeAccessoriesCheckbox();
        ImGui.SameLine();
        DrawIncludeGroupedSetsCheckbox();
        if (_config.IncludeGroupedSets && _gear.Building)
        {
            // Beside the toggles rather than on a line of its own, so the table doesn't jump when the scan finishes.
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("Finding more sets...");
        }

        if (!ImGui.BeginTable("###sets", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 300f);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        // Materialize the filtered list once, then clip. With the name-grouped sets the list runs to a few thousand rows,
        // and drawing every row's icons each frame would tank the framerate (the same reason the NPC tab clips).
        var filter = _setFilter.Trim().ToLowerInvariant();
        var all = AllSets();
        var visible = filter.Length == 0
            ? all
            : all.Where(s => s.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();

        var clipper = new ImGuiListClipper();
        clipper.Begin(visible.Count, IconSize + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var set = visible[row];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawIcon(set.Icon);
                ImGui.SameLine();
                if (ImGui.Selectable($"{set.Name}###set_{set.RowId}", false,
                        ImGuiSelectableFlags.None, new Vector2(0, IconSize)))
                    _pendingSet = set;
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    var count = _config.IncludeAccessories
                        ? set.Pieces.Count
                        : set.Pieces.Count(p => !OutfitService.IsAccessory(p.Slot));
                    ImGui.SetTooltip(set.Grouped
                        ? $"Apply \"{set.Name}\" to yourself ({count} pieces)\nGrouped by item name; the game doesn't list this as a set."
                        : $"Apply \"{set.Name}\" to yourself ({count} pieces)");
                }

                ImGui.TableNextColumn();
                foreach (var piece in set.Pieces)
                {
                    var dimmed = !_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot);
                    DrawIcon(piece.Icon, dimmed);
                    if (ImGui.IsItemClicked())
                        _pendingPiece = piece;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        DrawPreviewTooltip(piece.Icon, piece.Name, SlotLabel(piece.Slot), null, "Click to apply just this piece");
                    }
                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
        }
        clipper.End();

        ImGui.EndTable();
    }

    // ---- Loose gear tab -----------------------------------------------------------------------------------------------

    private const float LooseTileSize = 40f;
    private const float LooseTileGap  = 4f;
    private const float PreviewSize   = 112f;

    // Index-aligned with GearSlot (Facewear is the last one).
    private static readonly string[] LooseSlotLabels = { "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "Ring", "Facewear" };

    private void DrawLooseTab()
    {
        // The search box is the first row of every tab, so it sits at the same height on all of them.
        DrawFilterBox("###loosefilter", "Filter by item name", ref _looseFilter);

        var data = _gear.Data;
        if (data is null)
        {
            ImGui.TextDisabled(_gear.Error ?? "Building the gear list...");
            return;
        }

        var hide = _config.LooseHideDuplicateLooks;
        var slot = Math.Clamp(_config.LooseSlot, 0, LooseSlotLabels.Length - 1);
        DrawLooseSlotChips(ref slot);

        if (ImGui.Checkbox("Hide duplicate looks", ref hide))
        {
            _config.LooseHideDuplicateLooks = hide;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Many pieces are the same gear sold once per role (\"of Fending\", \"of Casting\", ...) and look identical.\n" +
                "On shows one tile for each; hover it to see the variants. Off shows every item.");

        var source = hide ? data.Looks[slot] : data.AllLooks[slot];
        var filter = _looseFilter.Trim().ToLowerInvariant();
        var visible = filter.Length == 0
            ? source
            : source.Where(l => l.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();

        // A count is only useful while a filter is narrowing the list, so it is shown only then, on the checkbox's row
        // so the grid never shifts as you type.
        if (filter.Length > 0)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(visible.Count == 1 ? "1 match" : $"{visible.Count} matches");
        }

        if (!ImGui.BeginChild("###loosegrid", new Vector2(0f, 0f)))
        {
            ImGui.EndChild();
            return;
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
        }
        else
        {
            // Wrapped icon grid, clipped by row so only the visible tiles are ever drawn.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(LooseTileGap, LooseTileGap));
            var availX = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
            var cols   = Math.Max(1, (int)((availX + LooseTileGap) / (LooseTileSize + LooseTileGap)));
            var rows   = (visible.Count + cols - 1) / cols;

            var clipper = new ImGuiListClipper();
            clipper.Begin(rows, LooseTileSize + LooseTileGap);
            while (clipper.Step())
            {
                for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    for (var c = 0; c < cols; c++)
                    {
                        var idx = r * cols + c;
                        if (idx >= visible.Count)
                            break;
                        if (c > 0)
                            ImGui.SameLine();
                        DrawLooseTile(visible[idx]);
                    }
                }
            }
            clipper.End();
            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
    }

    /// <summary>Single-select slot chips, reflowing onto further lines when the window is narrow.</summary>
    private void DrawLooseSlotChips(ref int slot)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var x = 0f;
        const float gap = 6f;
        for (var i = 0; i < LooseSlotLabels.Length; i++)
        {
            var label = LooseSlotLabels[i];
            var w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (i > 0)
            {
                if (x + gap + w > avail) x = 0f;
                else { ImGui.SameLine(0f, gap); x += gap; }
            }

            var on = slot == i;
            if (on)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.16f, 0.20f, 1f));
            if (ImGui.Button($"{label}###looseslot{i}"))
            {
                slot = i;
                _config.LooseSlot = i;
                Plugin.PluginInterface.SavePluginConfig(_config);
            }
            if (on)
                ImGui.PopStyleColor();
            x += w;
        }
    }

    private void DrawLooseTile(GearLook look)
    {
        var item = look.Representative;
        var size = new Vector2(LooseTileSize);

        if (_textures.TryGetFromGameIcon(new GameIconLookup(item.Icon), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);

        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
            _pendingLoose = item;
        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.GetColorU32(new Vector4(0.95f, 0.85f, 0.45f, 1f)), 3f);

        var detail = LooseSlotLabels[(int)item.Slot];
        if (item.Level > 0)
            detail += $"  |  Lv {item.Level}";          // facewear has no level
        if (item.JobText.Length > 0)
            detail += $"  |  {item.JobText}";

        // Other items that look exactly the same (shown when duplicate looks are collapsed).
        List<string>? same = null;
        if (look.Variants.Count > 1)
        {
            same = look.Variants.Where(v => v.ItemId != item.ItemId).Take(6).Select(v => v.Name).ToList();
            if (look.Variants.Count - 1 > same.Count)
                same.Add($"+{look.Variants.Count - 1 - same.Count} more");
        }

        DrawPreviewTooltip(item.Icon, item.Name, detail, same, "Click to apply");
    }

    /// <summary>
    /// The large preview shown on hover - the whole point of the Loose gear tab, since Glamourer itself only shows an
    /// icon for the piece you already have selected. <paramref name="sameLook"/> lists other items sharing this look.
    /// </summary>
    private void DrawPreviewTooltip(uint iconId, string title, string detail, IReadOnlyList<string>? sameLook, string hint)
    {
        ImGui.BeginTooltip();

        var size = new Vector2(PreviewSize);
        if (_textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(title);
        if (detail.Length > 0)
            ImGui.TextDisabled(detail);
        if (sameLook is { Count: > 0 })
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Also looks like:");
            foreach (var line in sameLook)
                ImGui.TextUnformatted(line);
        }
        ImGui.Spacing();
        ImGui.TextDisabled(hint);
        ImGui.EndGroup();

        ImGui.EndTooltip();
    }

    private static string SlotLabel(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Ears => "Earrings",
        ApiEquipSlot.Neck => "Necklace",
        ApiEquipSlot.Wrists => "Bracelet",
        ApiEquipSlot.RFinger or ApiEquipSlot.LFinger => "Ring",
        ApiEquipSlot.MainHand => "Main hand",
        ApiEquipSlot.OffHand => "Off hand",
        _ => slot.ToString(),
    };

    private void DoApplyLoose(GearItem item)
    {
        var ok = _gear.ApplyPiece(item, _glam, 0);
        SetStatus(ok,
            $"Applied \"{item.Name}\" ({LooseSlotLabels[(int)item.Slot]}).",
            $"Couldn't apply \"{item.Name}\" ({LooseSlotLabels[(int)item.Slot]}), see /xllog.");
    }

    private void DrawNpcTab()
    {
        // The search box is the first row of every tab, so it sits at the same height on all of them.
        DrawFilterBox("###npcfilter", "Filter by NPC, race, or clan name", ref _npcFilter);

        var mode = _config.NpcApplyMode;
        ImGui.TextUnformatted("Apply:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Both", ref mode, 0)) SaveMode(mode);
        ImGui.SameLine();
        if (ImGui.RadioButton("Appearance", ref mode, 1)) SaveMode(mode);
        ImGui.SameLine();
        if (ImGui.RadioButton("Gear", ref mode, 2)) SaveMode(mode);

        // Options row: apply-name, include-weapons, and include-accessories sit
        // together on one line below the mode radios.
        // Apply-name toggle: always visible. Enabled when Moniker (HMoniker
        // v2.1+) is detected; disabled with an explanation otherwise, so it's
        // never ambiguous whether the feature is missing or just off.
        var monikerAvailable = _moniker.Available;
        if (!monikerAvailable)
            ImGui.BeginDisabled();

        var applyName = _config.NpcApplyName;
        if (ImGui.Checkbox("Apply name", ref applyName))
        {
            _config.NpcApplyName = applyName;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }

        if (!monikerAvailable)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            if (monikerAvailable)
                ImGui.SetTooltip("Also set your nameplate to the NPC's name via Moniker.\nRevert clears it.");
            else
                ImGui.SetTooltip(
                    "Requires the Moniker (HMoniker) plugin, v2.1 or newer, to be installed and enabled.\n" +
                    "If you have it and this is still disabled, its IPC version may be older than 2.1.");
        }

        // Weapons are opt-in: they're written as custom-model appearances and
        // Glamourer can reject a weapon that isn't valid for your current class.
        ImGui.SameLine();
        var includeWeapons = _config.NpcIncludeWeapons;
        if (ImGui.Checkbox("Include weapons", ref includeWeapons))
        {
            _config.NpcIncludeWeapons = includeWeapons;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Experimental. When applying a whole NPC, also copy its main-hand and off-hand weapons.\n" +
                "Glamourer may ignore a weapon that isn't valid for your current class, so this can do\n" +
                "nothing for some weapons. Clicking a single weapon icon always applies regardless.");

        ImGui.SameLine();
        DrawIncludeAccessoriesCheckbox();

        if (!ImGui.BeginTable("###npcs", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn("Race / Clan", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        // Materialize the filtered list once, then clip. Without clipping, drawing
        // every one of thousands of NPC rows per frame tanks the framerate (this
        // is what Glamourer's list clipper avoids). We only render the rows the
        // clipper says are visible.
        var filter = _npcFilter.Trim().ToLowerInvariant();
        var visible = filter.Length == 0
            ? (IReadOnlyList<NpcEntry>)_npcs.Npcs
            : _npcs.Npcs.Where(n => n.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();

        var clipper = new ImGuiListClipper();
        clipper.Begin(visible.Count, IconSize + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var npc = visible[row];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{npc.Name}###npc_{npc.RowId}", false,
                        ImGuiSelectableFlags.None, new Vector2(0, IconSize)))
                    _pendingNpc = npc;
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip($"Apply {npc.Name} to yourself ({ModeLabel()}).");
                }
                // NPC id in a fainter font, right after the name — distinguishes
                // same-named NPCs and confirms which entry this is (Glamourer-style).
                ImGui.SameLine();
                ImGui.TextDisabled($"({npc.RowId})");

                ImGui.TableNextColumn();
                var gender = npc.IsFemale ? "\u2640" : "\u2642";
                var rc = npc.ClanName.Length > 0 && npc.ClanName != npc.RaceName
                    ? $"{gender} {npc.RaceName} — {npc.ClanName}"
                    : $"{gender} {npc.RaceName}";
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(rc);

                ImGui.TableNextColumn();
                var pieceIdx = 0;
                foreach (var piece in npc.Pieces)
                {
                    var dimmed = (!_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot))
                               || (!_config.NpcIncludeWeapons && NpcStateBuilder.IsWeapon(piece.Slot));
                    if (dimmed) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);

                    // Generic slot placeholder icon (Glamourer-style silhouette).
                    // The raw silhouettes are dark and vanish on the dark table, so
                    // draw a lighter rounded plate behind each via the draw list
                    // (stable API — avoids the version-sensitive ImageButton
                    // overloads). Falls back to a text button if the ULD icon is
                    // missing.
                    var icon = _slotIcons.Get(piece.Slot);
                    bool clicked;
                    ImGui.PushID($"{npc.RowId}_{pieceIdx}");
                    if (icon != null)
                    {
                        var pos = ImGui.GetCursorScreenPos();
                        var sz  = new Vector2(IconSize);
                        var dl  = ImGui.GetWindowDrawList();
                        // Rounded background plate + subtle rim.
                        dl.AddRectFilled(pos, pos + sz, ImGui.GetColorU32(new Vector4(0.24f, 0.24f, 0.27f, 1f)), 4f);
                        dl.AddRect(pos, pos + sz, ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.50f, 1f)), 4f);
                        ImGui.Image(icon.Handle, sz);
                        clicked = ImGui.IsItemClicked();
                    }
                    else
                    {
                        clicked = ImGui.Button($"{SlotAbbrev(piece.Slot)}", new Vector2(IconSize + 6, IconSize + 6));
                    }
                    ImGui.PopID();
                    if (clicked)
                        _pendingNpcPiece = (npc, piece);

                    if (dimmed) ImGui.PopStyleVar();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        ImGui.SetTooltip($"{piece.Slot} ({piece.Label}) — click to apply just this piece");
                    }
                    ImGui.SameLine();
                    pieceIdx++;
                }
                ImGui.NewLine();
            }
        }
        clipper.End();

        ImGui.EndTable();
    }

    private void SaveMode(int mode)
    {
        _config.NpcApplyMode = mode;
        Plugin.PluginInterface.SavePluginConfig(_config);
    }

    private string ModeLabel() => _config.NpcApplyMode switch
    {
        1 => "appearance only",
        2 => "gear only",
        _ => "appearance + gear",
    };

    private static string SlotAbbrev(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head => "Head",
        ApiEquipSlot.Body => "Body",
        ApiEquipSlot.Hands => "Hand",
        ApiEquipSlot.Legs => "Legs",
        ApiEquipSlot.Feet => "Feet",
        ApiEquipSlot.Ears => "Ear",
        ApiEquipSlot.Neck => "Neck",
        ApiEquipSlot.Wrists => "Wrist",
        ApiEquipSlot.RFinger => "R.Ring",
        ApiEquipSlot.LFinger => "L.Ring",
        ApiEquipSlot.MainHand => "M.Hand",
        ApiEquipSlot.OffHand => "O.Hand",
        _ => slot.ToString(),
    };

    private void DrawIcon(uint iconId, bool dimmed = false)
    {
        var size = new Vector2(IconSize);
        if (_textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
        {
            if (dimmed) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
            ImGui.Image(wrap.Handle, size);
            if (dimmed) ImGui.PopStyleVar();
        }
        else
            ImGui.Dummy(size);
    }

    private void DoApplySet(OutfitSet set)
    {
        var (applied, failed) = _outfits.Apply(set, _glam, 0, _config.IncludeAccessories);
        if (applied == 0 && failed == 0)
        {
            // e.g. an accessories-only set while "Include accessories" is off: nothing was attempted.
            SetStatus(false, string.Empty, $"\"{set.Name}\" only has accessories. Turn on Include accessories to apply it.");
            return;
        }
        SetStatus(failed == 0,
            $"Applied \"{set.Name}\" ({applied} pieces).",
            $"Applied \"{set.Name}\": {applied} ok, {failed} failed (see /xllog).");
    }

    private void DoApplyPiece(OutfitPiece piece)
    {
        var ok = _outfits.ApplyPiece(piece, _glam, 0);
        SetStatus(ok,
            $"Applied \"{piece.Name}\" ({piece.Slot}).",
            $"Couldn't apply \"{piece.Name}\" ({piece.Slot}) — see /xllog.");
    }

    private void DoApplyNpc(NpcEntry npc)
    {
        var mode = _config.NpcApplyMode switch
        {
            1 => NpcStateBuilder.Mode.AppearanceOnly,
            2 => NpcStateBuilder.Mode.GearOnly,
            _ => NpcStateBuilder.Mode.Both,
        };

        var ec = _npcState.Apply(npc, mode, _config.IncludeAccessories, _config.NpcIncludeWeapons);
        var ok = ec == Glamourer.Api.Enums.GlamourerApiEc.Success;

        var namePart = "";
        if (_config.NpcApplyName && _moniker.Available)
        {
            if (_moniker.SetLocalName(npc.Name))
            {
                _appliedNameThisSession = true;
                namePart = " + name";
            }
        }

        SetStatus(ok,
            $"Applied {npc.Name} ({ModeLabel()}{namePart}).",
            $"Apply {npc.Name} returned {(ec.HasValue ? ec.ToString() : "null")} — see /xllog.");
    }

    private void DoApplyNpcPiece(NpcEntry npc, NpcPiece piece)
    {
        var ec = _npcState.ApplyPiece(npc, piece);
        var ok = ec == Glamourer.Api.Enums.GlamourerApiEc.Success;
        SetStatus(ok,
            $"Applied {npc.Name}'s {piece.Slot} ({piece.Label}).",
            $"Couldn't apply that piece ({(ec.HasValue ? ec.ToString() : "null")}) — see /xllog.");
    }

    private void DoRevert()
    {
        var ec = _glam.Revert(0);
        var ok = ec == Glamourer.Api.Enums.GlamourerApiEc.Success;

        if (_appliedNameThisSession && _moniker.Available)
        {
            _moniker.ClearLocalName();
            _appliedNameThisSession = false;
        }

        SetStatus(ok, "Reverted to game state.", $"Revert failed ({ec}) — see /xllog.");
    }

    private void SetStatus(bool ok, string okMsg, string failMsg)
    {
        _status = ok ? okMsg : failMsg;
        _statusColor = ok ? new Vector4(0.4f, 0.85f, 0.45f, 1f) : new Vector4(0.95f, 0.75f, 0.35f, 1f);
    }
}
