using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lumina.Data.Files;

namespace HOutfits;

/// <summary>
/// One variant of a gear model that no item uses. <paramref name="SameAsEarlier"/> is set when it looks exactly like a lower
/// variant of the same model that is also listed here (the Loose gear tab shows each look once; a set row still needs it so
/// every piece of that variant is there).
/// </summary>
public readonly record struct ModelVariant(byte Variant, bool SameAsEarlier);

/// <summary>
/// The model variants the game has but no item uses: the NPC-only colours of player gear (Uwagi #55 has a purple variant 2 and
/// a white variant 3 that no item points at). They come from each model's IMC file, which lists every variant of the model
/// and, per variant and slot, the material it uses. Variant n is IMC entry n - 1; variant 0 is the default entry, which always
/// repeats a real one, so it is never listed. A variant is left out when:
/// <list type="bullet">
/// <item>an item already uses it, or it looks exactly like one that does (same material, decal, parts and effects);</item>
/// <item>it has no material for that slot (material 0, or a material folder with no files), which would draw broken.</item>
/// </list>
/// </summary>
public sealed class VariantIndex
{
    public static readonly VariantIndex Empty = new(new Dictionary<(bool, int, ushort), IReadOnlyList<ModelVariant>>());

    private static readonly IReadOnlyList<ModelVariant> None = Array.Empty<ModelVariant>();

    // Part order inside an IMC file, which is also the slot's file-name code.
    private static readonly string[] EquipmentParts = { "met", "top", "glv", "dwn", "sho" };
    private static readonly string[] AccessoryParts = { "ear", "nek", "wrs", "rir", "ril" };

    // Every playable race and body (and the two NPC-only bodies), most common first. A material folder only has files for
    // the bodies that model was made for, so finding any of them is enough.
    private static readonly string[] Races =
    {
        "0101", "0201", "0301", "0401", "0501", "0601", "0701", "0801", "0901", "1001", "1101", "1201",
        "1301", "1401", "1501", "1601", "1701", "1801", "0104", "0204", "9104", "9204",
    };

    private readonly IReadOnlyDictionary<(bool Accessory, int Part, ushort Set), IReadOnlyList<ModelVariant>> _byModel;

    private VariantIndex(IReadOnlyDictionary<(bool, int, ushort), IReadOnlyList<ModelVariant>> byModel) => _byModel = byModel;

    /// <summary>How many models have at least one unlisted variant.</summary>
    public int ModelCount => _byModel.Count;

    /// <summary>The unlisted variants of the model a Loose gear piece uses, lowest first.</summary>
    public IReadOnlyList<ModelVariant> For(GearSlot slot, ulong model)
        => PartOf(slot) is { } p && _byModel.TryGetValue((p.Accessory, p.Part, (ushort)(model & 0xFFFF)), out var list) ? list : None;

    /// <summary>The unlisted variants of the model a set piece uses, lowest first.</summary>
    public IReadOnlyList<ModelVariant> For(ApiEquipSlot slot, ulong model)
        => PartOf(slot) is { } p && _byModel.TryGetValue((p.Accessory, p.Part, (ushort)(model & 0xFFFF)), out var list) ? list : None;

    /// <summary>The model value with its variant replaced (armour and accessories keep the variant in bits 16 to 31).</summary>
    public static ulong WithVariant(ulong model, byte variant) => (model & ~0xFFFF0000ul) | ((ulong)variant << 16);

    private static (bool Accessory, int Part)? PartOf(GearSlot slot) => slot switch
    {
        GearSlot.Head   => (false, 0),
        GearSlot.Body   => (false, 1),
        GearSlot.Hands  => (false, 2),
        GearSlot.Legs   => (false, 3),
        GearSlot.Feet   => (false, 4),
        GearSlot.Ears   => (true, 0),
        GearSlot.Neck   => (true, 1),
        GearSlot.Wrists => (true, 2),
        GearSlot.Ring   => (true, 3),
        _               => null,
    };

    private static (bool Accessory, int Part)? PartOf(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head    => (false, 0),
        ApiEquipSlot.Body    => (false, 1),
        ApiEquipSlot.Hands   => (false, 2),
        ApiEquipSlot.Legs    => (false, 3),
        ApiEquipSlot.Feet    => (false, 4),
        ApiEquipSlot.Ears    => (true, 0),
        ApiEquipSlot.Neck    => (true, 1),
        ApiEquipSlot.Wrists  => (true, 2),
        ApiEquipSlot.RFinger => (true, 3),
        ApiEquipSlot.LFinger => (true, 4),
        _                    => null,
    };

    /// <summary>
    /// Reads the IMC file of every armour and accessory model in <paramref name="items"/> (about 1,800 files) and keeps the
    /// variants no item uses. <paramref name="cancelled"/> is checked between models so an unloading plugin isn't held up.
    /// </summary>
    public static VariantIndex Build(IDataManager data, IEnumerable<GearItem> items, Func<bool> cancelled)
    {
        // Which variants items already use, per model and slot.
        var used = new Dictionary<(bool, int, ushort), HashSet<int>>();
        foreach (var item in items)
        {
            if (PartOf(item.Slot) is not { } p)
                continue;
            var key = (p.Accessory, p.Part, (ushort)(item.Model & 0xFFFF));
            if (!used.TryGetValue(key, out var set))
                used[key] = set = new HashSet<int>();
            set.Add((int)((item.Model >> 16) & 0xFFFF));
        }

        var files  = new Dictionary<string, ImcFile?>();
        var result = new Dictionary<(bool, int, ushort), IReadOnlyList<ModelVariant>>();
        foreach (var ((accessory, part, model), itemVariants) in used)
        {
            if (cancelled())
                return Empty;

            var folder = accessory ? $"chara/accessory/a{model:D4}" : $"chara/equipment/e{model:D4}";
            var path   = $"{folder}/{(accessory ? 'a' : 'e')}{model:D4}.imc";
            if (!files.TryGetValue(path, out var imc))
                files[path] = imc = data.FileExists(path) ? data.GetFile<ImcFile>(path) : null;
            if (imc is null || (imc.PartMask & (1 << part)) == 0)
                continue;

            var entries = imc.GetPart(part);
            var looks   = new HashSet<(int, int, int, int, int)>();
            foreach (var v in itemVariants)
                if (Entry(entries, v) is { } e)
                    looks.Add(Look(e));

            var found = new List<ModelVariant>();
            var seen  = new HashSet<(int, int, int, int, int)>();
            for (var v = 1; v <= entries.Variants.Length && v <= byte.MaxValue; v++)
            {
                var e = entries.Variants[v - 1];
                if (itemVariants.Contains(v) || e.MaterialId == 0 || looks.Contains(Look(e)))
                    continue;
                var code = (accessory ? AccessoryParts : EquipmentParts)[part];
                if (!MaterialExists(data, folder, accessory, model, code, e.MaterialId))
                    continue;
                found.Add(new ModelVariant((byte)v, !seen.Add(Look(e))));
            }

            if (found.Count > 0)
                result[(accessory, part, model)] = found;
        }

        return new VariantIndex(result);
    }

    private static ImcFile.ImageChangeData? Entry(ImcFile.ImageChangeParts entries, int variant)
        => variant == 0 ? entries.DefaultVariant
         : variant <= entries.Variants.Length ? entries.Variants[variant - 1]
         : null;

    // What decides how a variant looks: the same values mean the same look.
    private static (int, int, int, int, int) Look(ImcFile.ImageChangeData e)
        => (e.MaterialId, e.DecalId, e.AttributeMask, e.VfxId, e.MaterialAnimationId);

    private static bool MaterialExists(IDataManager data, string folder, bool accessory, ushort model, string code, int material)
    {
        var prefix = accessory ? 'a' : 'e';
        foreach (var suffix in "abcde")
            foreach (var race in Races)
                if (data.FileExists($"{folder}/material/v{material:D4}/mt_c{race}{prefix}{model:D4}_{code}_{suffix}.mtrl"))
                    return true;
        return false;
    }
}
