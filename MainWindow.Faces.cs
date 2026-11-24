using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glamourer.Api.Enums;

namespace HOutfits;

/// <summary>
/// The Faces tab, two views (picked in the options row):
///  - Looks: the faces only NPCs have (Hythlodaeus's, 2B's...), one row per distinct look: a face number plus the values that
///    shape it (eyebrows, eyes, nose, jaw, mouth, facial features). A face number with several looks is a row you open. A face
///    is made for one body (see <see cref="FaceIndex"/>), so faces for your body come first; the others are dimmed but still
///    clickable (some work on another body, some come out invisible).
///    "Full head" also copies hair and colours.
///  - Parts: for the face you have now, each part value NPCs with the same face wear (and face paint from your whole body),
///    applied one part at a time.
/// Values character creation doesn't offer are gold. Glamourer shows these values once applied but won't let you type them.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Vector4 NpcOnlyColor = new(0.95f, 0.80f, 0.30f, 1f);

    private string _faceFilter = string.Empty;
    private bool _faceParts;                       // session only: Looks (false) or Parts (true)
    private (NpcEntry Npc, IReadOnlySet<string> Keys, string What)? _pendingFace;

    private IReadOnlyList<FaceGroup>? _faces;
    private IReadOnlyList<NpcEntry>? _facesFrom;
    private CreationLimits? _limits;
    private readonly HashSet<(byte, byte, bool, byte)> _expandedFaces = new();
    private int _faceGroupsVersion;

    // Your customize bytes as drawn, read at most once a second while the tab is open (they change when you apply something).
    private byte[]? _mine;
    private long _mineNextRead;                    // Environment.TickCount64 at which to read again; 0 = now

    private enum FaceRowKind { Single, Group, Member }

    private sealed record FaceRow(FaceRowKind Kind, FaceGroup Group, FaceLook Look, bool Fits);

    private List<FaceRow>? _faceRows;
    private (IReadOnlyList<FaceGroup>? Faces, string Filter, string Body, int Version) _faceRowsKey;

    private IReadOnlyList<FacePart>? _parts;
    private string _partsKey = string.Empty;

    private void DrawFacesOptions()
    {
        var parts = _faceParts ? 1 : 0;
        FlowText("Show:");
        FlowNext(CheckboxWidth("Looks"));
        if (ImGui.RadioButton("Looks", ref parts, 0)) _faceParts = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Whole faces only NPCs have, one row per look.");
        FlowNext(CheckboxWidth("Parts"));
        if (ImGui.RadioButton("Parts", ref parts, 1)) _faceParts = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For the face you have now: the eyebrows, eyes, nose, jaw, mouth, facial features and face paint\n" +
                             "NPCs with the same face wear, to change one part at a time.");

        if (_faceParts)
            return;
        FlowNext(CheckboxWidth("Full head"));
        var fullHead = _config.FacesFullHead;
        if (ImGui.Checkbox("Full head", ref fullHead))
        {
            _config.FacesFullHead = fullHead;
            Plugin.PluginInterface.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Off: only the face (its shape, eyebrows, eyes, nose, jaw, mouth and facial features).\n" +
                "On: the whole head, also the NPC's hair, eye and lip colours, tattoo colour and face paint.\n" +
                "Your skin colour, height and body stay yours either way.");
    }

    private void DrawFacesTab()
    {
        DrawFilterBox("###facefilter",
            _faceParts ? "Filter by part or NPC (e.g. \"eyebrows\")" : "Filter by NPC, race, clan, or face number (e.g. \"218\")",
            ref _faceFilter);

        var npcs = _npcs.Npcs;
        if (!ReferenceEquals(_facesFrom, npcs))
        {
            _faces     = FaceIndex.Build(npcs);
            _facesFrom = npcs;
            _partsKey  = string.Empty;
        }
        _limits ??= CreationLimits.Load(Plugin.DataManager, _log);

        var now = Environment.TickCount64;
        if (now >= _mineNextRead)
        {
            _mine         = _npcState.CurrentCustomize();
            _mineNextRead = now + 1000;
        }

        if (_mine is null)
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), "Couldn't read your character's appearance, so nothing can be applied.");

        if (_faceParts)
            DrawPartsTable(npcs);
        else
            DrawLooksTable();
    }

    // ── Looks ──

    private void DrawLooksTable()
    {
        var filter = _faceFilter.Trim().ToLowerInvariant();
        var rows   = FaceRows(filter);

        if (!ImGui.BeginTable("###faces", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Face", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Look", ImGuiTableColumnFlags.WidthFixed, 230f);
        ImGui.TableSetupColumn("Parts", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Made for", ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var height  = ImGui.GetFrameHeight();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count, height + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
            {
                var row   = rows[r];
                var group = row.Group;
                ImGui.TableNextRow();
                if (!row.Fits) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.6f);
                var id =$"{group.Race}_{group.Clan}_{group.IsFemale}_{group.Face}";

                ImGui.TableNextColumn();
                if (row.Kind == FaceRowKind.Group)
                {
                    var key  = (group.Race, group.Clan, group.IsFemale, group.Face);
                    var open = filter.Length > 0 || _expandedFaces.Contains(key);
                    ImGui.ArrowButton($"###facegrp_{id}", open ? ImGuiDir.Down : ImGuiDir.Right);
                    var toggle = ImGui.IsItemClicked();
                    ImGui.SameLine();
                    toggle |= ImGui.Selectable($"Face {group.Face}###facegrpname_{id}", false, ImGuiSelectableFlags.SpanAllColumns,
                        new Vector2(0, height));
                    if (toggle && filter.Length == 0)
                    {
                        if (!_expandedFaces.Remove(key))
                            _expandedFaces.Add(key);
                        _faceGroupsVersion++;
                    }
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled($"{group.Looks.Count} looks, e.g. {group.Looks[0].Best.Name}");
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(BodyLabel(group));
                    if (!row.Fits) ImGui.PopStyleVar();
                    continue;
                }

                if (row.Kind == FaceRowKind.Member)
                {
                    DrawGuide(height, height, ImGui.GetStyle().CellPadding.Y * 2f);
                    ImGui.Dummy(new Vector2(height, height));
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"Face {group.Face}");
                }

                ImGui.TableNextColumn();
                var look    = row.Look;
                var npc     = look.Best;
                var clicked = ImGui.Selectable($"{npc.Name}###facelook_{id}_{npc.RowId}", false, ImGuiSelectableFlags.None,
                    new Vector2(0, height));
                var hovered = ImGui.IsItemHovered();
                if (look.Wearers.Count > 1)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"+{look.Wearers.Count - 1}");
                    hovered |= ImGui.IsItemHovered();
                }
                if (hovered)
                    LookTooltip(group, look, row.Fits);
                if (clicked && _mine is not null)
                {
                    var full = _config.FacesFullHead;
                    _pendingFace = (npc, full ? NpcStateBuilder.HeadKeys : NpcStateBuilder.FaceKeys,
                        $"{npc.Name}'s {(full ? "head" : "face")} (face {group.Face})");
                }

                ImGui.TableNextColumn();
                DrawLookParts(npc.Customize, group.IsFemale);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(BodyLabel(group));

                if (!row.Fits) ImGui.PopStyleVar();
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private void LookTooltip(FaceGroup group, FaceLook look, bool fits)
    {
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Put this {(_config.FacesFullHead ? "head" : "face")} on you (face {group.Face}).");
        if (!fits)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), $"Made for a {BodyLabel(group)} body, not yours.");
            ImGui.TextDisabled("Some faces still work on another body; others come out invisible or broken. Revert undoes it.");
        }
        if (look.Wearers.Count > 1)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Worn by {look.Wearers.Count} NPCs:");
            ImGui.TextUnformatted(string.Join(", ", look.Wearers.Take(12).Select(w => w.Name))
                                  + (look.Wearers.Count > 12 ? $" +{look.Wearers.Count - 12} more" : ""));
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Gold values are ones character creation doesn't offer.");
        ImGui.EndTooltip();
    }

    /// <summary>"Brows 3  Eyes 2  Nose 1  Jaw 0  Mouth 4  Features 1, 3", values creation doesn't offer in gold.</summary>
    private void DrawLookParts(byte[] c, bool female)
    {
        ImGui.AlignTextToFramePadding();
        var first = true;
        foreach (var (label, idx, mask) in LookPartColumns)
        {
            var value = (byte)(c[idx] & mask);
            if (!first) ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 2f);
            first = false;
            ImGui.TextDisabled(label);
            ImGui.SameLine(0f, 4f);
            if (_limits!.IsNpcOnly(c[4], female, idx, value))
                ImGui.TextColored(NpcOnlyColor, value.ToString());
            else
                ImGui.TextUnformatted(value.ToString());
        }
        ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 2f);
        ImGui.TextDisabled("Features");
        ImGui.SameLine(0f, 4f);
        ImGui.TextUnformatted(FeatureList(c[12]));
    }

    private static readonly (string Label, int Byte, byte Mask)[] LookPartColumns =
    {
        ("Brows", 14, 0xFF), ("Eyes", 16, 0x7F), ("Nose", 17, 0xFF), ("Jaw", 18, 0xFF), ("Mouth", 19, 0x7F),
    };

    /// <summary>Facial features byte as "1, 3, tattoo" (bits 0-6 are the seven toggles, bit 7 the legacy tattoo), or "none".</summary>
    private static string FeatureList(byte b)
    {
        var parts = new List<string>(8);
        for (var i = 0; i < 7; i++)
            if ((b & (1 << i)) != 0)
                parts.Add((i + 1).ToString());
        if ((b & 0x80) != 0)
            parts.Add("tattoo");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string BodyLabel(FaceGroup face)
    {
        var gender = face.IsFemale ? "♀" : "♂";
        return face.ClanName.Length > 0 ? $"{gender} {face.RaceName} ({face.ClanName})" : $"{gender} {face.RaceName}";
    }

    /// <summary>
    /// The Looks table's rows: faces that fit your body first, then the rest. A face with one look is one row; a face with more
    /// is a header row with its looks under it while open (all open while searching). Rebuilt only on change.
    /// </summary>
    private List<FaceRow> FaceRows(string filter)
    {
        var mine = _mine;
        var body = mine is null ? string.Empty : $"{mine[0]}/{mine[1]}/{mine[4]}";
        if (_faceRows is not null && ReferenceEquals(_faceRowsKey.Faces, _faces) && _faceRowsKey.Filter == filter
            && _faceRowsKey.Body == body && _faceRowsKey.Version == _faceGroupsVersion)
            return _faceRows;

        var result = new List<FaceRow>();
        var groups = (_faces ?? Array.Empty<FaceGroup>())
            .Where(f => filter.Length == 0 || f.SearchText.Contains(filter, StringComparison.Ordinal))
            .Select(f => (Group: f, Fits: mine is not null && FaceIndex.Fits(f, mine[0], mine[4], mine[1] == 1)))
            .OrderByDescending(f => f.Fits);
        foreach (var (group, fits) in groups)
        {
            if (group.Looks.Count == 1)
            {
                result.Add(new FaceRow(FaceRowKind.Single, group, group.Looks[0], fits));
                continue;
            }
            result.Add(new FaceRow(FaceRowKind.Group, group, group.Looks[0], fits));
            if (filter.Length > 0 || _expandedFaces.Contains((group.Race, group.Clan, group.IsFemale, group.Face)))
                foreach (var look in filter.Length == 0 ? group.Looks : MatchingLooks(group, filter))
                    result.Add(new FaceRow(FaceRowKind.Member, group, look, fits));
        }

        _faceRows    = result;
        _faceRowsKey = (_faces, filter, body, _faceGroupsVersion);
        return result;
    }

    /// <summary>While searching by an NPC's name, only the looks that NPC wears; when the match was the face or race, all of them.</summary>
    private static IReadOnlyList<FaceLook> MatchingLooks(FaceGroup group, string filter)
    {
        var byName = group.Looks.Where(l => l.SearchText.Contains(filter, StringComparison.Ordinal)).ToList();
        return byName.Count > 0 ? byName : group.Looks;
    }

    // ── Parts ──

    private void DrawPartsTable(IReadOnlyList<NpcEntry> npcs)
    {
        var mine = _mine;
        if (mine is null)
            return;

        var key = Convert.ToHexString(mine);
        if (_parts is null || _partsKey != key)
        {
            _parts    = FaceIndex.Parts(npcs, mine);
            _partsKey = key;
        }

        var female = mine[1] == 1;
        ImGui.TextDisabled($"Your face is face {mine[5]}. Listed: what NPCs with this face and body wear (face paint: any NPC with your body).");

        var filter = _faceFilter.Trim().ToLowerInvariant();
        var parts  = filter.Length == 0
            ? _parts
            : _parts.Where(p => PartLabel(p.Kind).Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || p.Wearers.Any(w => w.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (parts.Count == 0)
        {
            ImGui.TextDisabled(_parts.Count == 0 ? "No NPC with this face and body wears anything different." : "Nothing matches the search.");
            return;
        }

        if (!ImGui.BeginTable("###faceparts", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Worn by", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var height  = ImGui.GetFrameHeight();
        var clipper = new ImGuiListClipper();
        clipper.Begin(parts.Count, height + ImGui.GetStyle().CellPadding.Y * 2);
        while (clipper.Step())
        {
            for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
            {
                var part = parts[r];
                ImGui.TableNextRow();

                // The part's name only on the first of its values, so the list reads as sections.
                ImGui.TableNextColumn();
                var label   = PartLabel(part.Kind);
                var shown   = r == 0 || parts[r - 1].Kind != part.Kind ? label : string.Empty;
                var clicked = ImGui.Selectable($"{shown}###facepart_{r}", false, ImGuiSelectableFlags.SpanAllColumns,
                    new Vector2(0, height));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip($"Change just your {label.ToLowerInvariant()} to {PartValue(part)}.\n" +
                                     $"Worn by: {string.Join(", ", part.Wearers.Take(12).Select(w => w.Name))}" +
                                     (part.Wearers.Count > 12 ? $" +{part.Wearers.Count - 12} more" : ""));
                }
                if (clicked)
                    _pendingFace = (part.Wearers[0], NpcStateBuilder.PartKeys(part.Kind), $"{label.ToLowerInvariant()} {PartValue(part)}");

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                var idx = PartByte(part.Kind);
                if (idx >= 0 && _limits!.IsNpcOnly(mine[4], female, idx, part.Value))
                    ImGui.TextColored(NpcOnlyColor, PartValue(part));
                else
                    ImGui.TextUnformatted(PartValue(part));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(string.Join(", ", part.Wearers.Take(6).Select(w => w.Name))
                                   + (part.Wearers.Count > 6 ? $" +{part.Wearers.Count - 6} more" : ""));
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private static string PartLabel(PartKind kind) => kind switch
    {
        PartKind.Eyebrows       => "Eyebrows",
        PartKind.EyeShape       => "Eye shape",
        PartKind.Nose           => "Nose",
        PartKind.Jaw            => "Jaw",
        PartKind.Mouth          => "Mouth",
        PartKind.FacialFeatures => "Facial features",
        _                       => "Face paint",
    };

    /// <summary>The customize byte a list-style part lives in, for the gold check; -1 for parts that aren't lists.</summary>
    private static int PartByte(PartKind kind) => kind switch
    {
        PartKind.Eyebrows => 14,
        PartKind.EyeShape => 16,
        PartKind.Nose     => 17,
        PartKind.Jaw      => 18,
        PartKind.Mouth    => 19,
        _                 => -1,
    };

    private static string PartValue(FacePart part) => part.Kind switch
    {
        PartKind.FacialFeatures => FeatureList(part.Value),
        PartKind.FacePaint      => $"{part.Value & 0x7F}{((part.Value & 0x80) != 0 ? " reversed" : "")}, colour {part.Extra}",
        _                       => part.Value.ToString(),
    };

    private void DoApplyFace(NpcEntry npc, IReadOnlySet<string> keys, string what)
    {
        var ec = _npcState.ApplyCustomize(npc, keys);
        _mineNextRead = 0;   // read your appearance again next frame
        SetStatus(ec == GlamourerApiEc.Success,
            $"Applied {what}.",
            $"Couldn't apply {what} ({ec?.ToString() ?? "no Glamourer state"}), see /xllog.");
    }
}
