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
/// The plugin window. Four tabs:
///  - Outfit sets: the game's named sets, plus (optionally) sets recovered from
///    item names, applied via Glamourer SetItem.
///  - Loose gear: wearable pieces that belong to no set at all, browsed by slot
///    as an icon grid with a large preview on hover, applied via Glamourer SetItem.
///  - Weapons: every weapon, browsed by class in the same kind of grid. Your own
///    class leads; the others can be previewed but only applied in GPose, because
///    Glamourer won't take another class's weapon otherwise.
///  - NPCs: named human NPCs, applied via Glamourer ApplyState (their gear has
///    no item id, so it's whole-state application, not SetItem).
///
/// The set-based tabs carry their own "Include accessories" toggle (bound to the
/// same setting, shown in-context beside each tab's other options) and all tabs
/// share the "Revert changes" button in the header. Draw callbacks stay
/// side-effect-light: clicks queue work that fires at the top of Draw next frame.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const float IconSize = 32f;

    private readonly OutfitService _outfits;
    private readonly GearService _gear;
    private readonly NpcService _npcs;
    private readonly NpcStateBuilder _npcState;
    private readonly GlamourerIpc _glam;
    private readonly MonikerIpc _moniker;
    private readonly PlayerContext _player;
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

    // "Show variants": each set's variant rows, built the first time they are needed, and the sets whose rows are expanded.
    // Every set starts collapsed, so the list stays as long as it is without variants until you open one.
    private readonly Dictionary<uint, IReadOnlyList<OutfitSet>> _setVariantRows = new();
    private readonly HashSet<uint> _expandedSets = new();

    // Loose gear and Weapons tabs: the piece a click queued for the next frame
    private GearItem? _pendingGear;

    // Loose gear tab state
    private string _looseFilter = string.Empty;

    // "Show variants": the selected slot's tiles with a "+n" badge on pieces that have variants (rebuilt only when the source
    // list changes), each model's variant tiles (built the first time they are needed), and the models that are expanded.
    // Every piece starts collapsed.
    private IReadOnlyList<GearLook>? _looseMarked;
    private IReadOnlyList<GearLook>? _looseMarkedFrom;
    private readonly Dictionary<(GearSlot, ushort), IReadOnlyList<GearLook>> _looseVariantTiles = new();
    private readonly HashSet<(GearSlot, ushort)> _expandedLooks = new();

    // Weapons tab state. Not saved: the tab always opens on the class you are playing, on the main hand.
    private string _weaponFilter = string.Empty;
    private uint _weaponClass;          // a class picked by hand; 0 follows the class you are on
    private bool _weaponOffHand;

    // NPC tab state
    private string _npcFilter = string.Empty;

    // NPC tab rows: NPCs with the same face grouped under one expandable row (see NpcGroupKey), rebuilt when the list, the
    // search or the expanded groups change; and, per look NPCs wear, who wears it (rebuilt with the NPC list).
    private readonly HashSet<string> _expandedNpcGroups = new();
    private List<NpcRow>? _npcRows;
    private (IReadOnlyList<NpcEntry>? List, string Filter, int Expanded) _npcRowsKey;
    private int _npcGroupsVersion;
    private Dictionary<(ApiEquipSlot, ulong), SortedSet<string>>? _npcWearers;
    private IReadOnlyList<NpcEntry>? _npcWearersFrom;
    private NpcEntry? _pendingNpc;
    private (NpcEntry npc, NpcPiece piece)? _pendingNpcPiece;

    // The EQP table (what a body piece hides), read the first time the NPCs tab needs it.
    private EqpTable? _eqp;
    private bool _eqpLoaded;

    // Shared
    private Tab _activeTab = Tab.Sets;
    private bool _pendingRevert;
    private bool _appliedNameThisSession;
    private string _status = string.Empty;
    private Vector4 _statusColor = new(0.7f, 0.7f, 0.7f, 1f);

    public MainWindow(OutfitService outfits, GearService gear, NpcService npcs, NpcStateBuilder npcState,
        GlamourerIpc glam, MonikerIpc moniker, PlayerContext player, ITextureProvider textures, SlotIconService slotIcons,
        IPluginLog log, Configuration config)
        : base("HOutfits###HOutfitsMain")
    {
        _outfits   = outfits;
        _gear      = gear;
        _npcs      = npcs;
        _npcState  = npcState;
        _glam      = glam;
        _moniker   = moniker;
        _player    = player;
        _textures  = textures;
        _slotIcons = slotIcons;
        _log       = log;
        _config    = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(1400, 2000),
        };

        // The title carries the version, so a screenshot says which build it is. The ### id keeps the window's saved
        // position and size across versions.
        var testBuild = false;
        MarkTestBuild(ref testBuild);
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        WindowName = $"HOutfits v{version}{(testBuild ? " (test build)" : "")}###HOutfitsMain";

        InitWeaponOverride();
    }

    public void Dispose() => DisposeWeaponOverride();

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
        DrawOptionsRow();

        if (!ImGui.BeginTabBar("###hotabs"))
            return;

        // Each tab records itself as the active one, so the options row above the tabs (drawn first, next frame) shows that
        // tab's toggles.
        if (ImGui.BeginTabItem("Outfit sets"))
        {
            _activeTab = Tab.Sets;
            DrawSetsTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Loose gear"))
        {
            _activeTab = Tab.Loose;
            DrawLooseTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Weapons"))
        {
            _activeTab = Tab.Weapons;
            DrawWeaponsTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("NPCs"))
        {
            _activeTab = Tab.Npcs;
            DrawNpcTab();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrainPendingActions()
    {
        if (_pendingSet is { } ps)          { _pendingSet = null;       DoApplySet(ps); }
        if (_pendingPiece is { } pp)        { _pendingPiece = null;     DoApplyPiece(pp); }
        if (_pendingGear is { } pg)         { _pendingGear = null;      DoApplyGear(pg); }
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
    }

    private enum Tab { Sets, Loose, Weapons, Npcs }

    /// <summary>
    /// The options row, between the header and the tabs: "Large icons" (every tab) and then the toggles of the tab that is
    /// open, so each tab's own area starts with its search box. Items flow left to right and wrap onto another line when the
    /// window is too narrow. The tabs keep only what picks what to look at: search, slot and class chips, Main/Off hand.
    /// The row is always at least two lines tall, so switching tabs never moves the tabs or anything under them.
    /// </summary>
    private void DrawOptionsRow()
    {
        var top = ImGui.GetCursorPosY();
        _flowFirst = true;
        FlowNext(CheckboxWidth("Large icons"));
        DrawLargeIconsCheckbox();

        switch (_activeTab)
        {
            case Tab.Sets:
                FlowNext(CheckboxWidth("Include accessories"));
                DrawIncludeAccessoriesCheckbox();
                FlowNext(CheckboxWidth("Include name-grouped sets"));
                DrawIncludeGroupedSetsCheckbox();
                DrawVariantOptions(sets: true);
                if ((_config.IncludeGroupedSets || _config.ShowVariants) && _gear.Building)
                    FlowText("Finding more sets...");
                break;

            case Tab.Loose:
                FlowNext(CheckboxWidth("Hide duplicate looks"));
                DrawHideDuplicatesCheckbox(
                    "Many pieces are the same gear sold once per role (\"of Fending\", \"of Casting\", ...) and look identical.\n" +
                    "On shows one tile for each; hover it to see the variants. Off shows every item.");
                FlowNext(CheckboxWidth("Include set items"));
                DrawIncludeSetItemsCheckbox();
                DrawVariantOptions(sets: false);
                break;

            case Tab.Weapons:
                FlowNext(CheckboxWidth("Hide duplicate looks"));
                DrawHideDuplicatesCheckbox(
                    "Many weapons look exactly the same: an upgraded copy of one weapon, or one made for a class and again for its job.\n" +
                    "On shows one tile for each; hover it to see the others. Off shows every item.");
                break;

            case Tab.Npcs:
                DrawNpcOptions();
                break;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var bottom  = top + 2f * (ImGui.GetFrameHeight() + spacing);
        if (ImGui.GetCursorPosY() < bottom - 0.5f)
            ImGui.Dummy(new Vector2(1f, bottom - ImGui.GetCursorPosY() - spacing));
        ImGui.Separator();
    }

    // Options-row flow layout: each item goes on the current line if it fits, otherwise on a new one.
    private bool _flowFirst;

    private void FlowNext(float width)
    {
        if (_flowFirst)
        {
            _flowFirst = false;
            return;
        }
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine();
    }

    private void FlowText(string text)
    {
        FlowNext(ImGui.CalcTextSize(text).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
    }

    private static float CheckboxWidth(string label)
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    private static float SmallButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

    /// <summary>"Show variants" and, while it is on, Expand all / Collapse all for the tab (and, on Loose gear, how to open one).</summary>
    private void DrawVariantOptions(bool sets)
    {
        FlowNext(CheckboxWidth("Show variants"));
        DrawShowVariantsCheckbox();
        if (!_config.ShowVariants)
            return;

        FlowNext(SmallButtonWidth("Expand all"));
        if (ImGui.SmallButton(sets ? "Expand all###setsexpand" : "Expand all###looseexpand"))
        {
            if (sets)
            {
                foreach (var s in AllSets())
                    if (VariantRows(s).Count > 0)
                        _expandedSets.Add(s.RowId);
            }
            else if (_gear.Data is { } data)
            {
                foreach (var look in LooseSource(data))
                    if (look.Badge?.StartsWith('+') == true)
                        _expandedLooks.Add((look.Representative.Slot, look.Representative.ModelSet));
            }
        }
        FlowNext(SmallButtonWidth("Collapse all"));
        if (ImGui.SmallButton(sets ? "Collapse all###setscollapse" : "Collapse all###loosecollapse"))
        {
            if (sets) _expandedSets.Clear();
            else _expandedLooks.Clear();
        }
        if (!sets)
            FlowText("Right-click a piece marked +n to expand it");
    }

    /// <summary>The NPCs tab's options: what applying an NPC copies, and how its pieces are labelled.</summary>
    private void DrawNpcOptions()
    {
        var mode = _config.NpcApplyMode;
        FlowText("Apply:");
        FlowNext(CheckboxWidth("Both"));
        if (ImGui.RadioButton("Both", ref mode, 0)) SaveMode(mode);
        FlowNext(CheckboxWidth("Appearance"));
        if (ImGui.RadioButton("Appearance", ref mode, 1)) SaveMode(mode);
        FlowNext(CheckboxWidth("Gear"));
        if (ImGui.RadioButton("Gear", ref mode, 2)) SaveMode(mode);

        // Apply-name toggle: always visible. Enabled when Moniker (HMoniker
        // v2.1+) is detected; disabled with an explanation otherwise, so it's
        // never ambiguous whether the feature is missing or just off.
        var monikerAvailable = _moniker.Available;
        FlowNext(CheckboxWidth("Apply name"));
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
        FlowNext(CheckboxWidth("Include weapons"));
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

        FlowNext(CheckboxWidth("Include accessories"));
        DrawIncludeAccessoriesCheckbox();

        FlowNext(CheckboxWidth("Show model IDs"));
        var showIds = _config.NpcShowModelIds;
        if (ImGui.Checkbox("Show model IDs", ref showIds))
        {
            _config.NpcShowModelIds = showIds;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Write each piece's full model number (e.g. 9055-3) under its icon.\n" +
                "Off, pieces with no item just show their variant (v3) in the corner.");
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
        var visible = WithExpandedVariants(filter.Length == 0
            ? all
            : all.Where(s => s.SearchText.Contains(filter, StringComparison.Ordinal)).ToList());

        var clipper = new ImGuiListClipper();
        clipper.Begin(visible.Count, ListIconSize + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var set = visible[row];
                var variantRow = set.Variant > 0;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (_config.ShowVariants)
                    DrawSetExpander(set);
                DrawIcon(set.Icon, variantRow ? ModelOnlyAlpha : 1f);
                if (variantRow)
                    DrawBadge($"v{set.Variant}");
                ImGui.SameLine();
                // A variant row sits right under its set, so it only needs to say which variant it is.
                var label = variantRow ? $"Variant {set.Variant}, no item" : set.Name;
                if (ImGui.Selectable($"{label}###set_{set.RowId}_{set.Variant}", false,
                        ImGuiSelectableFlags.None, new Vector2(0, ListIconSize)))
                    _pendingSet = set;
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    var count = _config.IncludeAccessories
                        ? set.Pieces.Count
                        : set.Pieces.Count(p => !OutfitService.IsAccessory(p.Slot));
                    ImGui.SetTooltip(variantRow
                        ? $"Apply \"{set.Name}\" in variant {set.Variant} to yourself ({count} pieces)\nNo item uses this look; it is applied by model."
                        : set.Grouped
                        ? $"Apply \"{set.Name}\" to yourself ({count} pieces)\nGrouped by item name; the game doesn't list this as a set."
                        : $"Apply \"{set.Name}\" to yourself ({count} pieces)");
                }

                ImGui.TableNextColumn();
                foreach (var piece in set.Pieces)
                {
                    var dimmed = !_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot);
                    DrawIcon(piece.Icon, dimmed ? 0.3f : variantRow ? ModelOnlyAlpha : 1f);
                    if (variantRow)
                        DrawBadge($"v{set.Variant}");
                    if (ImGui.IsItemClicked())
                        _pendingPiece = piece;
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        var detail = SlotLabel(piece.Slot);
                        if (piece.ItemId == 0)
                            detail += $"  |  model {piece.Model & 0xFFFF}-{(piece.Model >> 16) & 0xFFFF}  |  no item";
                        DrawPreviewTooltip(piece.Icon, piece.Name, detail, null, "Click to apply just this piece");
                    }
                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
        }
        clipper.End();

        ImGui.EndTable();
    }

    // ---- Loose gear and Weapons tabs ----------------------------------------------------------------------------------
    // Both are a row of chips above a wrapped grid of icon tiles with a large preview on hover, so they share the chip, grid,
    // tile and preview code below.

    private const float TileGap = 4f;
    // Tile + hover-preview size double with the options row's "Large icons" tickbox; both grid tabs read it. Game icons are a
    // fixed resolution, so the large size is upscaled (a little soft).
    private float IconScale   => _config.LargeIcons ? 2f : 1f;
    private float TileSize    => 40f  * IconScale;
    private float PreviewSize => 112f * IconScale;

    // Index-aligned with GearSlot.
    private static readonly string[] SlotNames =
        { "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "Ring", "Facewear", "Main hand", "Off hand" };

    /// <summary>One button in a chip row. A dimmed chip is greyed out but can still be clicked.</summary>
    private readonly record struct Chip(string Label, string Id, bool Dimmed = false, string? Tooltip = null);

    // The Loose gear chips: every slot up to and including Facewear (the two weapon slots belong to the Weapons tab).
    private static readonly Chip[] LooseChips = Enumerable.Range(0, (int)GearSlot.Facewear + 1)
        .Select(i => new Chip(SlotNames[i], $"looseslot{i}"))
        .ToArray();

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

        var slot = Math.Clamp(_config.LooseSlot, 0, LooseChips.Length - 1);
        var clicked = DrawChips(LooseChips, slot);
        if (clicked >= 0)
        {
            slot = clicked;
            _config.LooseSlot = clicked;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }

        var source  = LooseSource(data);
        var filter  = Normalize(_looseFilter);
        var visible = Filtered(source, filter);
        DrawMatchCount(visible.Count, filter);

        DrawTileGrid("###loosegrid", visible, enabled: true, "Click to apply",
            children: _config.ShowVariants && _expandedLooks.Count > 0 ? ExpandedVariants : null);
    }

    /// <summary>The Loose gear tiles of the selected slot, as the toggles have them, with variant badges.</summary>
    private IReadOnlyList<GearLook> LooseSource(GearData data)
    {
        var slot     = Math.Clamp(_config.LooseSlot, 0, LooseChips.Length - 1);
        var dedup    = _config.LooseHideDuplicateLooks;
        var withSets = _config.LooseIncludeSetItems;
        return MarkVariants(withSets
            ? (dedup ? data.LooksWithSets[slot] : data.AllLooksWithSets[slot])
            : (dedup ? data.Looks[slot]         : data.AllLooks[slot]), data.Variants);
    }

    // ---- Weapons tab --------------------------------------------------------------------------------------------------

    private void DrawWeaponsTab()
    {
        // The search box is the first row of every tab, so it sits at the same height on all of them.
        DrawFilterBox("###weaponfilter", "Filter by weapon name", ref _weaponFilter);

        var data = _gear.Data;
        if (data is null)
        {
            ImGui.TextDisabled(_gear.Error ?? "Building the gear list...");
            return;
        }

        var index = data.Weapons;
        if (index.Classes.Count == 0)
        {
            ImGui.TextDisabled("Couldn't read the weapon list (see /xllog).");
            return;
        }

        var active  = _player.ActiveJobId;
        var inGpose = _player.InGpose;

        // Your own class always leads (see WeaponIndex.ChipOrder), then the rest in the game's order.
        var ids = index.ChipOrder(active);

        // Outside GPose Glamourer only applies your own class's weapons, so the other chips are greyed out: you can still
        // open them and browse, but their weapons are preview only.
        var chips = new List<Chip>(ids.Count);
        foreach (var id in ids)
        {
            var info = index.Info[id];
            var mine = id == active;
            chips.Add(new Chip(info.Abbreviation, $"weaponclass{id}",
                Dimmed: !mine && !inGpose,
                Tooltip: mine ? $"{info.Name} (your current class)"
                    : inGpose ? info.Name
                    : $"{info.Name}\nPreview only: outside GPose Glamourer applies just your current class's weapons."));
        }

        var selected = _weaponClass != 0 && ids.Contains(_weaponClass) ? _weaponClass : ids[0];
        var clicked = DrawChips(chips, ids.IndexOf(selected), setApartFirst: ids[0] == active);
        if (clicked >= 0)
        {
            selected = ids[clicked];
            _weaponClass = selected == active ? 0u : selected;   // your own class is "follow me", so a job change keeps up
        }

        var canApply = inGpose || selected == active;
        var weapons  = index.ByClass[selected];

        // Off hands (shields, a crafter's second tool) only exist for some classes; for the others "Off hand" is greyed out.
        // Always drawing both keeps this row (and the grid under it) in the same place for every class.
        var hasOffHand = weapons.OffHand.Count > 0;
        var offHand    = hasOffHand && _weaponOffHand;
        if (ImGui.RadioButton("Main hand", !offHand)) { _weaponOffHand = false; offHand = false; }
        ImGui.SameLine();
        if (!hasOffHand)
            ImGui.BeginDisabled();
        if (ImGui.RadioButton("Off hand", offHand)) { _weaponOffHand = true; offHand = true; }
        if (!hasOffHand)
            ImGui.EndDisabled();
        if (!hasOffHand && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("This class has no off hand.");

        var hide   = _config.LooseHideDuplicateLooks;
        var source = offHand ? (hide ? weapons.OffHand : weapons.AllOffHand) : (hide ? weapons.MainHand : weapons.AllMainHand);
        var filter = Normalize(_weaponFilter);
        var visible = Filtered(source, filter);
        DrawMatchCount(visible.Count, filter);
        DrawWeaponOverride(ref canApply);

        if (!canApply)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), "Preview only");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Glamourer only applies the weapons of the class you are on, except in GPose.\nYou can still browse these.");
        }

        // One grid per class and hand, so switching chips starts at the top of that list instead of wherever the last one was scrolled.
        DrawTileGrid($"###weaponsgrid_{selected}_{(offHand ? "off" : "main")}", visible, canApply, canApply
            ? "Click to apply"
            : "Preview only.\nOutside GPose Glamourer applies just\nyour current class's weapons.",
            "Nothing matches for this class.");
    }

    // ---- Shared by both tabs ------------------------------------------------------------------------------------------

    /// <summary>
    /// Single-select chips, reflowing onto further lines when the window is narrow. Returns the index of the chip clicked this
    /// frame, or -1. <paramref name="setApartFirst"/> leaves a wider gap after the first chip (the class you are on).
    /// </summary>
    private static int DrawChips(IReadOnlyList<Chip> chips, int selected, bool setApartFirst = false)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var x = 0f;
        var clicked = -1;
        for (var i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            var w = ImGui.CalcTextSize(chip.Label).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (i > 0)
            {
                var gap = i == 1 && setApartFirst ? 16f : 6f;
                if (x + gap + w > avail) x = 0f;
                else { ImGui.SameLine(0f, gap); x += gap; }
            }

            var on = selected == i;
            if (on)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.16f, 0.20f, 1f));
            if (chip.Dimmed)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            if (ImGui.Button($"{chip.Label}###{chip.Id}"))
                clicked = i;
            if (chip.Dimmed)
                ImGui.PopStyleVar();                 // before the tooltip, so that isn't dimmed too
            if (on)
                ImGui.PopStyleColor();
            if (chip.Tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(chip.Tooltip);
            x += w;
        }
        return clicked;
    }

    /// <summary>
    /// The "Hide duplicate looks" toggle both grid tabs share (one saved setting).
    /// <paramref name="tooltip"/> says what a duplicate is on that tab.
    /// </summary>
    private void DrawHideDuplicatesCheckbox(string tooltip)
    {
        var hide = _config.LooseHideDuplicateLooks;
        if (ImGui.Checkbox("Hide duplicate looks", ref hide))
        {
            _config.LooseHideDuplicateLooks = hide;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>
    /// Loose gear tab: "Include set items" merges pieces that belong to a set (the game's own and the name-grouped ones)
    /// into the per-slot list, so one list and one search cover set and non-set gear together. Clicking a set piece still
    /// applies just that piece; whole sets stay on the Outfit sets tab.
    /// </summary>
    private void DrawIncludeSetItemsCheckbox()
    {
        var include = _config.LooseIncludeSetItems;
        if (ImGui.Checkbox("Include set items", ref include))
        {
            _config.LooseIncludeSetItems = include;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Also list pieces that belong to a set (the game's own and name-grouped), so one list per slot\n" +
                "covers set and loose gear alike. Off shows only pieces that belong to no set.\n" +
                "Clicking any piece applies just that piece; whole sets live on the Outfit sets tab.");
    }

    /// <summary>
    /// "Show variants", on the Outfit sets and Loose gear tabs (one shared setting): also list the model variants no item uses,
    /// next to the set or piece they are a variant of.
    /// </summary>
    private void DrawShowVariantsCheckbox()
    {
        var show = _config.ShowVariants;
        if (ImGui.Checkbox("Show variants", ref show))
        {
            _config.ShowVariants = show;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Also list the colours of a piece that no item uses, mostly worn by NPCs\n" +
                "(Uwagi #55 has a purple variant 2 and a white variant 3).\n" +
                "They have no icon of their own, so they show their item's icon faded, with the variant number.");
    }

    // A variant no item uses shows its item's icon at this opacity. VariantColor is the guide line under an expanded set on
    // the Outfit sets tab. On Loose gear each expanded piece in a row takes the next DrawerColors entry for its frame and
    // its drawer, so stacked drawers can be told apart.
    private const float ModelOnlyAlpha = 0.55f;

    private static readonly Vector4 VariantColor = new(0.40f, 0.65f, 0.95f, 0.9f);

    private static readonly Vector4[] DrawerColors =
    {
        VariantColor,
        new(0.95f, 0.70f, 0.30f, 0.9f),
        new(0.45f, 0.85f, 0.55f, 0.9f),
    };

    /// <summary>A small label in the bottom-right corner of the item just drawn (a tile or an icon). Returns its rectangle.</summary>
    private static (Vector2 Min, Vector2 Max) DrawBadge(string text)
    {
        var max  = ImGui.GetItemRectMax();
        var size = ImGui.CalcTextSize(text);
        var pos  = new Vector2(max.X - size.X - 3f, max.Y - size.Y - 1f);
        var min  = pos - new Vector2(2f, 0f);
        var end  = pos + size + new Vector2(2f, 0f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, end, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f)), 2f);
        draw.AddText(pos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), text);
        return (min, end);
    }

    /// <summary>A vertical guide line from just above the cursor down one row, centred in a column <paramref name="width"/> wide.</summary>
    private static void DrawGuide(float width, float height, float above)
    {
        var p = ImGui.GetCursorScreenPos();
        var x = p.X + width * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, p.Y - above), new Vector2(x, p.Y + height), ImGui.GetColorU32(VariantColor), 2f);
    }

    /// <summary>
    /// One row per variant of a set's models that no item uses, holding the pieces that have that variant. Built the first time
    /// a set needs it; empty without the variant scan, and for a variant row itself.
    /// </summary>
    private IReadOnlyList<OutfitSet> VariantRows(OutfitSet set)
    {
        var variants = _gear.Data?.Variants;
        if (variants is null || variants.ModelCount == 0 || set.Variant > 0)
            return Array.Empty<OutfitSet>();
        if (_setVariantRows.TryGetValue(set.RowId, out var rows))
            return rows;

        var result  = new List<OutfitSet>();
        var numbers = set.Pieces.SelectMany(p => variants.For(p.Slot, p.Model)).Select(v => v.Variant).Distinct().OrderBy(v => v);
        foreach (var v in numbers)
        {
            var pieces = set.Pieces
                .Where(p => variants.For(p.Slot, p.Model).Any(m => m.Variant == v))
                .Select(p => new OutfitPiece(0, $"{p.Name} (variant {v})", p.Icon, p.Slot, VariantIndex.WithVariant(p.Model, v)))
                .ToList();
            result.Add(set with { Pieces = pieces, Variant = v });
        }

        _setVariantRows[set.RowId] = result;
        return result;
    }

    /// <summary>The sets with the variant rows of every expanded set under it. Unchanged while nothing is expanded.</summary>
    private IReadOnlyList<OutfitSet> WithExpandedVariants(IReadOnlyList<OutfitSet> sets)
    {
        if (!_config.ShowVariants || _expandedSets.Count == 0)
            return sets;

        var result = new List<OutfitSet>(sets.Count + 16);
        foreach (var set in sets)
        {
            result.Add(set);
            if (_expandedSets.Contains(set.RowId))
                result.AddRange(VariantRows(set));
        }
        return result;
    }

    /// <summary>
    /// The arrow column of the Outfit sets table: an expand arrow on a set that has variants, a guide line on a variant row
    /// (which is also indented one step, so it reads as part of the set above), and an empty space otherwise.
    /// </summary>
    private void DrawSetExpander(OutfitSet set)
    {
        var arrow = ImGui.GetFrameHeight();
        if (set.Variant > 0)
        {
            DrawGuide(arrow, ListIconSize, ImGui.GetStyle().CellPadding.Y * 2f);
            ImGui.Dummy(new Vector2(arrow + ListIconSize * 0.5f, ListIconSize));
        }
        else if (VariantRows(set) is { Count: > 0 } rows)
        {
            var open = _expandedSets.Contains(set.RowId);
            if (ImGui.ArrowButton($"###setexp_{set.RowId}", open ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if (open) _expandedSets.Remove(set.RowId);
                else _expandedSets.Add(set.RowId);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{(open ? "Hide" : "Show")} {rows.Count} variant{(rows.Count == 1 ? "" : "s")} no item uses");
        }
        else
        {
            ImGui.Dummy(new Vector2(arrow, arrow));
        }
        ImGui.SameLine();
    }

    /// <summary>
    /// With "Show variants", the Loose gear tiles with a "+n" badge on each piece whose model has variants no item uses. Only
    /// a model's first tile gets it, so an item listed twice (duplicate looks shown) doesn't offer them twice.
    /// </summary>
    private IReadOnlyList<GearLook> MarkVariants(IReadOnlyList<GearLook> source, VariantIndex variants)
    {
        if (!_config.ShowVariants || variants.ModelCount == 0)
            return source;
        if (ReferenceEquals(_looseMarkedFrom, source) && _looseMarked is not null)
            return _looseMarked;

        var result = new List<GearLook>(source.Count);
        var done   = new HashSet<(GearSlot, ushort)>();
        foreach (var look in source)
        {
            var item  = look.Representative;
            var count = variants.For(item.Slot, item.Model).Count(v => !v.SameAsEarlier);
            result.Add(count == 0 || !done.Add((item.Slot, item.ModelSet))
                ? look
                : new GearLook { Representative = item, Variants = look.Variants, SearchText = look.SearchText, Badge = $"+{count}" });
        }

        _looseMarked     = result;
        _looseMarkedFrom = source;
        return result;
    }

    /// <summary>The variant tiles to show under a Loose gear piece, or null when it has none or they are collapsed.</summary>
    private IReadOnlyList<GearLook>? ExpandedVariants(GearLook look)
    {
        var item = look.Representative;
        var key  = (item.Slot, item.ModelSet);
        if (look.Badge?.StartsWith('+') != true || !_expandedLooks.Contains(key) || _gear.Data?.Variants is not { } variants)
            return null;
        if (_looseVariantTiles.TryGetValue(key, out var tiles))
            return tiles;

        tiles = variants.For(item.Slot, item.Model)
            .Where(v => !v.SameAsEarlier)
            .Select(v =>
            {
                var variant = item with
                {
                    ItemId = 0, Name = $"{item.Name} (variant {v.Variant})", Model = VariantIndex.WithVariant(item.Model, v.Variant),
                    ModelOnly = true,
                };
                return new GearLook { Representative = variant, Variants = new[] { variant }, SearchText = look.SearchText, Badge = $"v{v.Variant}" };
            })
            .ToList();
        _looseVariantTiles[key] = tiles;
        return tiles;
    }

    private void ToggleVariants(GearItem item)
    {
        var key = (item.Slot, item.ModelSet);
        if (!_expandedLooks.Remove(key))
            _expandedLooks.Add(key);
    }

    /// <summary>
    /// The "Large icons" tickbox, first on the options row of every tab: the gear icons of every tab (and the Loose gear and
    /// Weapons hover preview) at twice the default size, back to the default when unticked.
    /// </summary>
    private void DrawLargeIconsCheckbox()
    {
        var large = _config.LargeIcons;
        if (ImGui.Checkbox("Large icons", ref large))
        {
            _config.LargeIcons = large;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Doubles the size of the gear icons (and the Loose gear and Weapons hover preview) on every tab.\n" +
                "Game icons are a fixed resolution, so the large size is a little soft.");
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();

    private static IReadOnlyList<GearLook> Filtered(IReadOnlyList<GearLook> source, string filter)
        => filter.Length == 0
            ? source
            : source.Where(l => l.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// A count is only useful while a filter is narrowing the list, so it is shown only then, on the options row so the grid
    /// never shifts as you type.
    /// </summary>
    private static void DrawMatchCount(int count, string filter)
    {
        if (filter.Length == 0)
            return;
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(count == 1 ? "1 match" : $"{count} matches");
    }

    /// <summary>
    /// The wrapped icon grid, clipped by row so only the visible tiles are ever drawn. With <paramref name="enabled"/> off the
    /// tiles are dimmed and can't be clicked (they still preview on hover), and <paramref name="hint"/> is what that preview
    /// says in place of "Click to apply". <paramref name="empty"/> is shown when there is nothing to list.
    /// <paramref name="children"/> gives the tiles to show under a tile, if any (expanded variants); they open in a drawer
    /// under that tile's row (see <see cref="DrawNestedTileGrid"/>).
    /// </summary>
    private void DrawTileGrid(string id, IReadOnlyList<GearLook> visible, bool enabled, string hint, string empty = "Nothing matches.",
        Func<GearLook, IReadOnlyList<GearLook>?>? children = null)
    {
        if (children is not null && visible.Count > 0)
        {
            DrawNestedTileGrid(id, visible, enabled, hint, children);
            return;
        }

        if (!ImGui.BeginChild(id, new Vector2(0f, 0f)))
        {
            ImGui.EndChild();
            return;
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled(empty);
        }
        else
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(TileGap, TileGap));
            var availX = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
            var cols   = Math.Max(1, (int)((availX + TileGap) / (TileSize + TileGap)));
            var rows   = (visible.Count + cols - 1) / cols;

            var clipper = new ImGuiListClipper();
            clipper.Begin(rows, TileSize + TileGap);
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
                        DrawGearTile(visible[idx], enabled, hint);
                    }
                }
            }
            clipper.End();
            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// <see cref="DrawTileGrid"/> with children, laid out as drawers: the grid keeps its ordinary rows, and under a row that
    /// holds an expanded tile a tinted panel opens with that tile's children, indented half a tile. Each expanded tile in a row gets its own colour
    /// (its frame and its drawer), and the drawer right under the row is also joined to its tile by a tab. Laid out per frame,
    /// since the panels depend on which tiles are expanded and on the window width.
    /// </summary>
    private void DrawNestedTileGrid(string id, IReadOnlyList<GearLook> visible, bool enabled, string hint,
        Func<GearLook, IReadOnlyList<GearLook>?> children)
    {
        if (!ImGui.BeginChild(id, new Vector2(0f, 0f)))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(TileGap, TileGap));
        var availX = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
        var cols   = Math.Max(1, (int)((availX + TileGap) / (TileSize + TileGap)));

        // Flat tile list plus the rows over it. A drawer row knows the column of the tile it opened from, its colour, and
        // whether it is its drawer's first or last row; a tile knows its frame colour (-1 for none).
        var tiles = new List<(GearLook Look, int Frame)>(visible.Count + 16);
        var rows  = new List<(int Start, int Count, int Column, int Color, bool First, bool Last, bool Tab)>(visible.Count / cols + 8);
        var kids  = new IReadOnlyList<GearLook>?[cols];
        for (var i = 0; i < visible.Count; i += cols)
        {
            var count = Math.Min(cols, visible.Count - i);
            var start = tiles.Count;
            var open  = 0;
            for (var c = 0; c < count; c++)
            {
                kids[c] = children(visible[i + c]);
                tiles.Add((visible[i + c], kids[c] is { Count: > 0 } ? open++ % DrawerColors.Length : -1));
            }
            rows.Add((start, count, -1, -1, false, false, false));

            var drawer = 0;
            for (var c = 0; c < count; c++)
            {
                if (kids[c] is not { Count: > 0 } list)
                    continue;
                var color = drawer % DrawerColors.Length;
                var perRow = Math.Max(1, cols - 1);              // the indent takes about one tile's width
                for (var k = 0; k < list.Count; k += perRow)
                {
                    var first = tiles.Count;
                    var n     = Math.Min(perRow, list.Count - k);
                    for (var j = 0; j < n; j++)
                        tiles.Add((list[k + j], -1));
                    rows.Add((first, n, c, color, k == 0, k + n >= list.Count, k == 0 && drawer == 0));
                }
                drawer++;
            }
        }

        var step  = TileSize + TileGap;
        var width = cols * step - TileGap;
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count, step);
        while (clipper.Step())
        {
            for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
            {
                var (first, count, column, color, firstRow, lastRow, tab) = rows[r];
                if (column >= 0)
                {
                    // The drawer: a band of its colour behind this row, a line along its top and bottom, and (for the drawer
                    // right under the row) a tab up to the tile it belongs to, with the top line broken around it.
                    var tint = ImGui.GetColorU32(DrawerColors[color] with { W = 0.16f });
                    var edge = ImGui.GetColorU32(DrawerColors[color]);
                    var p    = ImGui.GetCursorScreenPos();
                    var draw = ImGui.GetWindowDrawList();
                    var top  = p.Y - TileGap * 0.5f;
                    var bot  = p.Y + TileSize + TileGap * 0.5f;
                    draw.AddRectFilled(new Vector2(p.X - 2f, top), new Vector2(p.X + width + 2f, bot), tint);
                    if (tab)
                    {
                        var x = p.X + column * step;
                        draw.AddRectFilled(new Vector2(x - 2f, top - TileGap), new Vector2(x + TileSize + 2f, top), tint);
                        draw.AddLine(new Vector2(p.X - 2f, top), new Vector2(x - 2f, top), edge, 1.5f);
                        draw.AddLine(new Vector2(x + TileSize + 2f, top), new Vector2(p.X + width + 2f, top), edge, 1.5f);
                    }
                    else if (firstRow)
                        draw.AddLine(new Vector2(p.X - 2f, top), new Vector2(p.X + width + 2f, top), edge, 1.5f);
                    if (lastRow)
                        draw.AddLine(new Vector2(p.X - 2f, bot), new Vector2(p.X + width + 2f, bot), edge, 1.5f);

                    // Variants sit indented inside their drawer, so they don't line up with the grid's own columns.
                    ImGui.Dummy(new Vector2(TileSize * 0.5f, TileSize));
                    ImGui.SameLine();
                }
                for (var k = 0; k < count; k++)
                {
                    if (k > 0)
                        ImGui.SameLine();
                    DrawGearTile(tiles[first + k].Look, enabled, hint, tiles[first + k].Frame);
                }
            }
        }
        clipper.End();
        ImGui.PopStyleVar();

        ImGui.EndChild();
    }

    private void DrawGearTile(GearLook look, bool enabled, string hint, int frame = -1)
    {
        var item = look.Representative;
        var size = new Vector2(TileSize);

        // A variant no item uses borrows its item's icon, drawn faded so it reads as "a version of that one".
        var alpha = !enabled ? 0.4f : item.ModelOnly ? ModelOnlyAlpha : 1f;
        if (alpha < 1f)
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
        if (_textures.TryGetFromGameIcon(new GameIconLookup(item.Icon), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);
        if (alpha < 1f)
            ImGui.PopStyleVar();

        // An expanded piece is framed in the colour of its drawer.
        if (frame >= 0)
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(DrawerColors[frame]), 3f, ImDrawFlags.None, 2f);
        var badgeRect = look.Badge is { } badge ? DrawBadge(badge) : default;

        // A piece with variants expands or collapses them from its "+n" badge, or a right-click anywhere on it; a plain click
        // elsewhere on the tile still applies it.
        var expandable = _config.ShowVariants && look.Badge?.StartsWith('+') == true;
        var onBadge    = expandable && ImGui.IsMouseHoveringRect(badgeRect.Min, badgeRect.Max);
        var hovered    = ImGui.IsItemHovered();
        if (expandable && (ImGui.IsItemClicked(ImGuiMouseButton.Right) || (onBadge && ImGui.IsItemClicked())))
            ToggleVariants(item);
        else if (enabled && ImGui.IsItemClicked())
            _pendingGear = item;
        if (!hovered)
            return;

        if (expandable)
        {
            var open = _expandedLooks.Contains((item.Slot, item.ModelSet));
            hint += $"\nClick {look.Badge} or right-click to {(open ? "hide" : "show")} its variants.";
        }

        // The rim marks the tile under the cursor: gold when a click will apply it, grey when it is preview only.
        if (enabled)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.GetColorU32(enabled ? new Vector4(0.95f, 0.85f, 0.45f, 1f) : new Vector4(0.6f, 0.6f, 0.62f, 1f)), 3f);

        var detail = SlotNames[(int)item.Slot];
        if (item.ModelOnly)
            detail += $"  |  model {item.ModelSet}-{item.ModelVariant}  |  no item";
        else
        {
            if (item.Level > 0)
                detail += $"  |  Lv {item.Level}";      // facewear has no level
            if (item.JobText.Length > 0)
                detail += $"  |  {item.JobText}";
        }

        // Other items that look exactly the same (shown when duplicate looks are collapsed).
        List<string>? same = null;
        if (look.Variants.Count > 1)
        {
            same = look.Variants.Where(v => v.ItemId != item.ItemId).Take(6).Select(v => v.Name).ToList();
            if (look.Variants.Count - 1 > same.Count)
                same.Add($"+{look.Variants.Count - 1 - same.Count} more");
        }

        DrawPreviewTooltip(item.Icon, item.Name, detail, same, hint);
    }

    /// <summary>
    /// The large preview shown on hover - the whole point of the Loose gear and Weapons tabs, since Glamourer itself only
    /// shows an icon for the piece you already have selected. <paramref name="sameLook"/> lists other items sharing this look.
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

    private void DoApplyGear(GearItem item)
    {
        if (item.ModelOnly)
        {
            var applied = _npcState.ApplyModels(new[] { (GearService.ToApi(item.Slot), item.Model) });
            SetStatus(applied == GlamourerApiEc.Success,
                $"Applied \"{item.Name}\" ({SlotNames[(int)item.Slot]}).",
                $"Couldn't apply \"{item.Name}\" ({applied?.ToString() ?? "no Glamourer state"}), see /xllog.");
            return;
        }

        var handled = false;
        TryApplyWeaponOverride(item, ref handled);
        if (handled)
            return;

        var slot = SlotNames[(int)item.Slot];
        var ec = _gear.ApplyPiece(item, _glam, 0);
        if (ec == GlamourerApiEc.Success)
        {
            SetStatus(true, $"Applied \"{item.Name}\" ({slot}).", string.Empty);
            return;
        }

        // A weapon is the one thing Glamourer can turn down by rule (outside GPose it limits which ones it applies), so say so.
        SetStatus(false, string.Empty,
            !item.IsWeapon ? $"Couldn't apply \"{item.Name}\" ({slot}), see /xllog."
            : ec == GlamourerApiEc.NothingDone ? $"\"{item.Name}\" changed nothing: you already wear it, or Glamourer won't take it."
            : $"Glamourer turned down \"{item.Name}\" ({ec}); outside GPose it limits which weapons it applies.");
    }

    // Extension point for a local build (marks the title as a test build); with no implementation the compiler removes the call.
    partial void MarkTestBuild(ref bool testBuild);

    private void DrawNpcTab()
    {
        // The search box is the first row of every tab, so it sits at the same height on all of them.
        DrawFilterBox("###npcfilter", "Filter by NPC, race, clan, or model (e.g. \"9055\")", ref _npcFilter);

        if (!ImGui.BeginTable("###npcs", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableSetupColumn("Race / Clan", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        // Materialize the filtered list once, then clip. Without clipping, drawing
        // every one of thousands of NPC rows per frame tanks the framerate (this
        // is what Glamourer's list clipper avoids). We only render the rows the
        // clipper says are visible.
        var filter = _npcFilter.Trim().ToLowerInvariant();
        var npcs = _npcs.Npcs;
        var visible = filter.Length == 0
            ? (IReadOnlyList<NpcEntry>)npcs
            : npcs.Where(n => n.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();
        if (!ReferenceEquals(_npcWearersFrom, npcs))
        {
            _npcWearers     = NpcLook.Wearers(npcs);
            _npcWearersFrom = npcs;
        }
        var rows = NpcRows(visible, filter);

        var size = ListIconSize;
        // With "Show model IDs" each piece has its model number under it, so the rows grow by a line of text.
        var rowHeight = size + (_config.NpcShowModelIds ? ImGui.GetTextLineHeight() : 0f);
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count, rowHeight + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
            {
                var row = rows[r];
                var npc = row.Npc;
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var arrow = ImGui.GetFrameHeight();
                if (row.Kind == NpcRowKind.Group)
                {
                    // A group's row only opens and closes it; its looks are the rows under it.
                    var open = filter.Length > 0 || _expandedNpcGroups.Contains(row.GroupKey);
                    ImGui.ArrowButton($"###npcgrp_{row.GroupKey}", open ? ImGuiDir.Down : ImGuiDir.Right);
                    var toggle = ImGui.IsItemClicked();
                    ImGui.SameLine();
                    toggle |= ImGui.Selectable($"{row.Name}###npcgrpname_{row.GroupKey}", false, ImGuiSelectableFlags.None,
                        new Vector2(0, size));
                    if (toggle && filter.Length == 0)
                    {
                        if (!_expandedNpcGroups.Remove(row.GroupKey))
                            _expandedNpcGroups.Add(row.GroupKey);
                        _npcGroupsVersion++;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            $"{row.Members.Count} NPC entries with the same face and hair, which usually means the same character in\n" +
                            "different outfits (sometimes a disguise, or a few characters who share a face).\n" +
                            (row.Names.Count > 1 ? $"Names: {string.Join(", ", row.Names)}\n" : "") +
                            "Click to show or hide them.");
                }
                else
                {
                    if (row.Kind == NpcRowKind.Member)
                    {
                        DrawGuide(arrow, rowHeight, ImGui.GetStyle().CellPadding.Y * 2f);
                        ImGui.Dummy(new Vector2(arrow + 10f, size));
                    }
                    else
                        ImGui.Dummy(new Vector2(arrow, size));
                    ImGui.SameLine();
                    if (ImGui.Selectable($"{npc.Name}###npc_{npc.RowId}", false,
                            ImGuiSelectableFlags.None, new Vector2(0, size)))
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
                }

                ImGui.TableNextColumn();
                var gender = npc.IsFemale ? "\u2640" : "\u2642";
                var rc = npc.ClanName.Length > 0 && npc.ClanName != npc.RaceName
                    ? $"{gender} {npc.RaceName} — {npc.ClanName}"
                    : $"{gender} {npc.RaceName}";
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(rc);

                ImGui.TableNextColumn();
                if (row.Kind == NpcRowKind.Group)
                {
                    ImGui.AlignTextToFramePadding();
                    var others = row.Names.Where(n => n != row.Name).ToList();
                    ImGui.TextDisabled(others.Count == 0
                        ? $"{row.Members.Count} looks"
                        : $"{row.Members.Count} looks, also as {string.Join(", ", others.Take(3))}{(others.Count > 3 ? $" +{others.Count - 3} more" : "")}");
                    continue;
                }

                var pieceIdx = 0;
                foreach (var piece in npc.Pieces)
                {
                    // In a group, outline the pieces no other look in the group wears: what sets this outfit apart.
                    var distinct = row.Kind == NpcRowKind.Member
                        && row.Members.Count(m => m.Pieces.Any(p => NpcLook.Key(p) == NpcLook.Key(piece))) == 1;
                    DrawNpcPiece(npc, piece, pieceIdx++, distinct);
                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
        }
        clipper.End();

        ImGui.EndTable();
    }

    // The icon size in the Outfit sets and NPCs tables; doubles with "Large icons" like the grids' tiles.
    private float ListIconSize => IconSize * IconScale;

    private enum NpcRowKind { Single, Group, Member }

    /// <summary>One row of the NPC table: an NPC on its own, a group's header, or one of a group's members.</summary>
    private sealed record NpcRow(NpcRowKind Kind, NpcEntry Npc, string GroupKey, string Name, IReadOnlyList<string> Names,
        IReadOnlyList<NpcEntry> Members);

    /// <summary>
    /// What groups NPC entries. Names are unreliable both ways (Nero is also "Nero tol Scaeva" and "Iron Nero", and different
    /// characters can share a name), but a face from the NPC-only range (200 and up) with its race, clan, sex and hairstyle
    /// picks out one character across names. Below 200 the faces are the generic ones thousands of NPCs share, so there the
    /// name is part of the key too.
    /// </summary>
    private static string NpcGroupKey(NpcEntry npc)
    {
        var c    = npc.Customize;
        var face = $"{c[0]}/{c[1]}/{c[4]}/{c[5]}/{c[6]}";     // race, sex, clan, face, hairstyle
        return c[5] >= 200 ? "face:" + face : $"name:{npc.Name}|{face}";
    }

    /// <summary>
    /// The table's rows: an entry whose group has no one else stays a plain row; a group of several gets a header row named
    /// after its most common name, with its members under it while it is open. While searching, every group is open.
    /// </summary>
    private List<NpcRow> NpcRows(IReadOnlyList<NpcEntry> visible, string filter)
    {
        var key = (visible, filter, _npcGroupsVersion);
        if (_npcRows is not null && ReferenceEquals(_npcRowsKey.List, visible) && _npcRowsKey.Filter == filter
            && _npcRowsKey.Expanded == _npcGroupsVersion)
            return _npcRows;

        var groups = visible.GroupBy(NpcGroupKey).ToList();
        var result = new List<NpcRow>(visible.Count);
        foreach (var g in groups
                     .Select(g => (Key: g.Key, Members: (IReadOnlyList<NpcEntry>)g.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.RowId).ToList()))
                     .Select(g => (g.Key, g.Members, Name: g.Members.GroupBy(m => m.Name)
                         .OrderByDescending(n => n.Count()).ThenBy(n => n.Key.Length).First().Key))
                     .OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            var names = g.Members.Select(m => m.Name).Distinct().ToList();
            if (g.Members.Count == 1)
            {
                result.Add(new NpcRow(NpcRowKind.Single, g.Members[0], g.Key, g.Name, names, g.Members));
                continue;
            }
            result.Add(new NpcRow(NpcRowKind.Group, g.Members[0], g.Key, g.Name, names, g.Members));
            if (filter.Length > 0 || _expandedNpcGroups.Contains(g.Key))
                foreach (var m in g.Members)
                    result.Add(new NpcRow(NpcRowKind.Member, m, g.Key, g.Name, names, g.Members));
        }

        _npcRows    = result;
        _npcRowsKey = key;
        return result;
    }

    /// <summary>
    /// One piece of an NPC's gear: the item's own icon when the look is an item, otherwise the slot's silhouette. With "Show
    /// model IDs" the model number is written under every piece; without it, a piece with no item shows its variant (v3) in
    /// the corner. A gold dot marks a look no other NPC wears; a frame marks one no other look in its group wears. Click
    /// applies the piece; right-click searches for every NPC who wears it.
    /// </summary>
    private void DrawNpcPiece(NpcEntry npc, NpcPiece piece, int index, bool distinct)
    {
        var dimmed = (!_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot))
                   || (!_config.NpcIncludeWeapons && NpcStateBuilder.IsWeapon(piece.Slot));
        var sz   = new Vector2(ListIconSize);
        var item = _gear.Data?.Items.For(piece);

        // With model IDs on, every piece is a column of one fixed width (room for a typical armour number, "65535-255"), the
        // icon and its number centred in it, so the icons line up from row to row whatever their numbers. A rare longer weapon
        // number spills a little into the gap.
        var showId  = _config.NpcShowModelIds;
        var idWidth = showId ? ImGui.CalcTextSize(piece.Label).X : 0f;
        var column  = showId ? Math.Max(sz.X, ImGui.CalcTextSize("65535-255").X) : sz.X;
        if (showId)
        {
            ImGui.BeginGroup();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (column - sz.X) * 0.5f);
        }

        if (dimmed) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
        ImGui.PushID($"{npc.RowId}_{index}");
        var pos = ImGui.GetCursorScreenPos();
        var dl  = ImGui.GetWindowDrawList();
        if (item is not null && _textures.TryGetFromGameIcon(new GameIconLookup(item.Icon), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, sz);
        else if (_slotIcons.Get(piece.Slot) is { } icon)
        {
            // The raw silhouettes are dark and vanish on the dark table, so they sit on a lighter rounded plate.
            dl.AddRectFilled(pos, pos + sz, ImGui.GetColorU32(new Vector4(0.24f, 0.24f, 0.27f, 1f)), 4f);
            dl.AddRect(pos, pos + sz, ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.50f, 1f)), 4f);
            ImGui.Image(icon.Handle, sz);
        }
        else
            ImGui.Button(SlotAbbrev(piece.Slot), sz);
        var clicked = ImGui.IsItemClicked();
        var right   = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        if (item is null && !showId)
            DrawBadge($"v{piece.Variant}");
        var key   = NpcLook.Key(piece);
        var names = _npcWearers is not null && _npcWearers.TryGetValue(key, out var w) ? w : null;
        if (names is { Count: 1 })
            dl.AddCircleFilled(new Vector2(pos.X + sz.X - 5f, pos.Y + 5f), 3.5f, ImGui.GetColorU32(new Vector4(0.95f, 0.80f, 0.30f, 1f)));
        if (distinct)
            dl.AddRect(pos, pos + sz, ImGui.GetColorU32(DrawerColors[1]), 4f, ImDrawFlags.None, 2f);
        if (showId)
        {
            // Tight under the icon, so the number reads as its caption.
            var y = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y;
            ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + (column - idWidth) * 0.5f, y));
            ImGui.TextDisabled(piece.Label);
            ImGui.SameLine(0f, 0f);
            ImGui.Dummy(new Vector2(Math.Max(0f, (column - idWidth) * 0.5f), 0f));   // the group always spans the full column
            ImGui.EndGroup();
        }
        if (dimmed) ImGui.PopStyleVar();

        if (clicked)
            _pendingNpcPiece = (npc, piece);
        if (right)
            _npcFilter = piece.Label;
        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(item is not null ? item.Name : $"Model {piece.Label}, no item");
        ImGui.TextDisabled(item is not null ? $"{SlotLabel(piece.Slot)}  |  model {piece.Label}" : SlotLabel(piece.Slot));
        var others = names?.Where(n => !string.Equals(n, npc.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
        ImGui.Spacing();
        if (others.Count == 0)
            ImGui.TextUnformatted("No other NPC wears this.");
        else
        {
            ImGui.TextDisabled($"Also worn by {others.Count} other NPC{(others.Count == 1 ? "" : "s")}:");
            ImGui.TextUnformatted(string.Join(", ", others.Take(8)) + (others.Count > 8 ? $" +{others.Count - 8} more" : ""));
        }
        if (distinct)
            ImGui.TextDisabled("Only this look in the group wears it.");
        if (piece.Slot == ApiEquipSlot.Body)
        {
            if (!_eqpLoaded)
            {
                _eqp       = EqpTable.Load(Plugin.DataManager, _log);
                _eqpLoaded = true;
            }
            if (_eqp?.BodyCovers(piece.Model) is { Length: > 0 } covers)
                ImGui.TextDisabled($"Also hides your {covers}.");
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Click to apply just this piece. Right-click to list every NPC who wears it.");
        ImGui.EndTooltip();
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

    private void DrawIcon(uint iconId, bool dimmed = false) => DrawIcon(iconId, dimmed ? 0.3f : 1f);

    private void DrawIcon(uint iconId, float alpha)
    {
        var size = new Vector2(ListIconSize);
        if (_textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
        {
            if (alpha < 1f) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            ImGui.Image(wrap.Handle, size);
            if (alpha < 1f) ImGui.PopStyleVar();
        }
        else
            ImGui.Dummy(size);
    }

    private void DoApplySet(OutfitSet set)
    {
        if (set.Variant > 0)
        {
            var pieces = set.Pieces.Where(p => _config.IncludeAccessories || !OutfitService.IsAccessory(p.Slot)).ToList();
            if (pieces.Count == 0)
            {
                SetStatus(false, string.Empty, $"\"{set.Name}\" variant {set.Variant} only has accessories. Turn on Include accessories to apply it.");
                return;
            }
            var ec = _npcState.ApplyModels(pieces.Select(p => (p.Slot, p.Model)));
            SetStatus(ec == GlamourerApiEc.Success,
                $"Applied \"{set.Name}\" variant {set.Variant} ({pieces.Count} pieces).",
                $"Couldn't apply \"{set.Name}\" variant {set.Variant} ({ec?.ToString() ?? "no Glamourer state"}), see /xllog.");
            return;
        }

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
        var ok = piece.ItemId == 0
            ? _npcState.ApplyModels(new[] { (piece.Slot, piece.Model) }) == GlamourerApiEc.Success
            : _outfits.ApplyPiece(piece, _glam, 0);
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
