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
/// The plugin window. Two tabs:
///  - Outfit sets: the game's named sets, applied via Glamourer SetItem.
///  - NPCs: named human NPCs, applied via Glamourer ApplyState (their gear has
///    no item id, so it's whole-state application, not SetItem).
///
/// Both tabs share the "Include accessories" toggle and the "Revert changes"
/// button. Draw callbacks stay side-effect-light: clicks queue work that fires
/// at the top of Draw next frame.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float IconSize = 32f;

    private readonly OutfitService _outfits;
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

    // NPC tab state
    private string _npcFilter = string.Empty;
    private NpcEntry? _pendingNpc;
    private (NpcEntry npc, NpcPiece piece)? _pendingNpcPiece;

    // Shared
    private bool _pendingRevert;
    private bool _appliedNameThisSession;
    private string _status = string.Empty;
    private Vector4 _statusColor = new(0.7f, 0.7f, 0.7f, 1f);

    public MainWindow(OutfitService outfits, NpcService npcs, NpcStateBuilder npcState,
        GlamourerIpc glam, MonikerIpc moniker, ITextureProvider textures, SlotIconService slotIcons,
        IPluginLog log, Configuration config)
        : base("HOutfits###HOutfitsMain")
    {
        _outfits   = outfits;
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

        DrawSharedHeader();

        if (!ImGui.BeginTabBar("###hotabs"))
            return;

        if (ImGui.BeginTabItem("Outfit sets"))
        {
            DrawSetsTab();
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
        if (_pendingNpc is { } pn)          { _pendingNpc = null;       DoApplyNpc(pn); }
        if (_pendingNpcPiece is { } pnp)    { _pendingNpcPiece = null;  DoApplyNpcPiece(pnp.npc, pnp.piece); }
        if (_pendingRevert)                 { _pendingRevert = false;   DoRevert(); }
    }

    private void DrawSharedHeader()
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

        ImGui.SameLine();
        if (ImGui.Button("Revert changes"))
            _pendingRevert = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Returns to game state (equipment, appearance, and any applied name).");

        if (_status.Length > 0)
            ImGui.TextColored(_statusColor, _status);

        ImGui.Separator();
    }

    private void DrawSetsTab()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###setfilter", "Filter by set or item name (e.g. \"ushanka\")", ref _setFilter, 128);

        if (!ImGui.BeginTable("###sets", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 300f);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var filter = _setFilter.Trim().ToLowerInvariant();
        foreach (var set in _outfits.Sets)
        {
            if (filter.Length > 0 && !set.SearchText.Contains(filter, StringComparison.Ordinal))
                continue;

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
                ImGui.SetTooltip($"Apply \"{set.Name}\" to yourself ({count} pieces)");
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
                    ImGui.SetTooltip($"{piece.Name}\n({piece.Slot}) — click to apply just this piece");
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        ImGui.EndTable();
    }

    private void DrawNpcTab()
    {
        var mode = _config.NpcApplyMode;
        ImGui.TextUnformatted("Apply:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Both", ref mode, 0)) SaveMode(mode);
        ImGui.SameLine();
        if (ImGui.RadioButton("Appearance", ref mode, 1)) SaveMode(mode);
        ImGui.SameLine();
        if (ImGui.RadioButton("Gear", ref mode, 2)) SaveMode(mode);

        // Apply-name toggle — always visible. Enabled when Moniker (HMoniker
        // v2.1+) is detected; disabled with an explanation otherwise, so it's
        // never ambiguous whether the feature is missing or just off.
        ImGui.SameLine();
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

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###npcfilter", "Filter by NPC, race, or clan name", ref _npcFilter, 128);

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
                    var dimmed = !_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot);
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

        var ec = _npcState.Apply(npc, mode, _config.IncludeAccessories);
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
