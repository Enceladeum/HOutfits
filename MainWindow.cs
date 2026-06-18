using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace HOutfits;

/// <summary>
/// The plugin window. Lists outfit sets (set icon + name + the component-piece
/// icons) and applies them to yourself via Glamourer instead of the fitting
/// room. Click a set name to apply the whole set; click a single piece icon to
/// add just that piece (additive — your other slots are untouched).
///
/// Draw callbacks stay side-effect-light per the Dalamud pattern: a click only
/// queues work; the actual IPC fires at the top of Draw on the next frame (still
/// the framework thread, so game/IPC access is safe).
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float IconSize = 32f;

    private readonly OutfitService _outfits;
    private readonly GlamourerIpc _glam;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;
    private readonly Configuration _config;

    private string _filter = string.Empty;
    private OutfitSet? _pendingSet;
    private OutfitPiece? _pendingPiece;
    private bool _pendingRevert;
    private string _status = string.Empty;
    private Vector4 _statusColor = new(0.7f, 0.7f, 0.7f, 1f);

    public MainWindow(OutfitService outfits, GlamourerIpc glam, ITextureProvider textures, IPluginLog log, Configuration config)
        : base("HOutfits###HOutfitsMain")
    {
        _outfits  = outfits;
        _glam     = glam;
        _textures = textures;
        _log      = log;
        _config   = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(1200, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Deferred apply: fire any IPC queued from a click last frame.
        if (_pendingSet is { } pendingSet)
        {
            _pendingSet = null;
            DoApplySet(pendingSet);
        }
        if (_pendingPiece is { } pendingPiece)
        {
            _pendingPiece = null;
            DoApplyPiece(pendingPiece);
        }
        if (_pendingRevert)
        {
            _pendingRevert = false;
            DoRevert();
        }

        if (!_glam.Available)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f),
                "Glamourer isn't loaded (or is too old). Install/update it to apply outfits.");
            return;
        }

        // Accessories toggle — governs WHOLE-SET applies only. When off, a set
        // skips earrings/necklace/bracelets/rings; clicking a single accessory
        // icon still applies it. Persisted so the choice sticks across sessions.
        var includeAccessories = _config.IncludeAccessories;
        if (ImGui.Checkbox("Include accessories", ref includeAccessories))
        {
            _config.IncludeAccessories = includeAccessories;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When off, applying a whole set skips earrings, necklace, bracelets, and rings.\n" +
                "Click an individual accessory icon to apply just that piece regardless.");

        // Revert button — top of window, always visible. Mirrors the in-game
        // "/glamour revert <me>": full revert (equipment + customization) to game.
        if (ImGui.Button("Revert changes"))
            _pendingRevert = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Returns to game state");

        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###filter", "Filter by set or item name (e.g. \"ushanka\")", ref _filter, 128);

        if (_status.Length > 0)
            ImGui.TextColored(_statusColor, _status);

        ImGui.Separator();

        if (!ImGui.BeginTable("###outfits", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 300f);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        // Filter matches the set name OR any piece name (SearchText is the set
        // name + every piece name, pre-lowercased).
        var filter = _filter.Trim().ToLowerInvariant();
        foreach (var set in _outfits.Sets)
        {
            if (filter.Length > 0 && !set.SearchText.Contains(filter, StringComparison.Ordinal))
                continue;

            ImGui.TableNextRow();

            // --- Set column: icon + name; clicking applies the whole set ---
            ImGui.TableNextColumn();
            DrawIcon(set.Icon);
            ImGui.SameLine();
            if (ImGui.Selectable($"{set.Name}###set_{set.RowId}", false,
                    ImGuiSelectableFlags.None, new Vector2(0, IconSize)))
            {
                _pendingSet = set; // act next frame
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var count = _config.IncludeAccessories
                    ? set.Pieces.Count
                    : set.Pieces.Count(p => !OutfitService.IsAccessory(p.Slot));
                ImGui.SetTooltip($"Apply \"{set.Name}\" to yourself ({count} pieces)");
            }

            // --- Pieces column: each icon is individually clickable ---
            ImGui.TableNextColumn();
            foreach (var piece in set.Pieces)
            {
                var dimmed = !_config.IncludeAccessories && OutfitService.IsAccessory(piece.Slot);
                DrawIcon(piece.Icon, dimmed);
                if (ImGui.IsItemClicked())
                    _pendingPiece = piece; // apply just this piece, next frame
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

    private void DrawIcon(uint iconId, bool dimmed = false)
    {
        var size = new Vector2(IconSize);
        if (_textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var tex)
            && tex.TryGetWrap(out var wrap, out _))
        {
            // Dim (but keep clickable) the accessory icons a set-apply will skip
            // while "Include accessories" is off. Alpha multiplies the image tint,
            // so the icon greys out; hit-testing is unaffected.
            if (dimmed) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
            ImGui.Image(wrap.Handle, size);
            if (dimmed) ImGui.PopStyleVar();
        }
        else
            ImGui.Dummy(size);
    }

    private void DoApplySet(OutfitSet set)
    {
        // objectIndex 0 == the local player. "Always self", per the design.
        var (applied, failed) = _outfits.Apply(set, _glam, objectIndex: 0, _config.IncludeAccessories);

        if (failed == 0)
        {
            _status = $"Applied \"{set.Name}\" ({applied} pieces).";
            _statusColor = new Vector4(0.4f, 0.85f, 0.45f, 1f);
        }
        else
        {
            _status = $"Applied \"{set.Name}\": {applied} ok, {failed} failed (see /xllog).";
            _statusColor = new Vector4(0.95f, 0.75f, 0.35f, 1f);
        }
    }

    private void DoRevert()
    {
        var ec = _glam.Revert(objectIndex: 0);
        if (ec == Glamourer.Api.Enums.GlamourerApiEc.Success)
        {
            _status = "Reverted to game state.";
            _statusColor = new Vector4(0.4f, 0.85f, 0.45f, 1f);
        }
        else
        {
            _status = $"Revert failed ({ec}) — see /xllog.";
            _statusColor = new Vector4(0.95f, 0.75f, 0.35f, 1f);
        }
    }

    private void DoApplyPiece(OutfitPiece piece)
    {
        var ok = _outfits.ApplyPiece(piece, _glam, objectIndex: 0);
        if (ok)
        {
            _status = $"Applied \"{piece.Name}\" ({piece.Slot}).";
            _statusColor = new Vector4(0.4f, 0.85f, 0.45f, 1f);
        }
        else
        {
            _status = $"Couldn't apply \"{piece.Name}\" ({piece.Slot}) — see /xllog.";
            _statusColor = new Vector4(0.95f, 0.75f, 0.35f, 1f);
        }
    }
}
