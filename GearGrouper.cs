using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HOutfits;

/// <summary>
/// The wearable (non-weapon) slots the Loose gear tab covers. Facewear is last and is a different kind of thing: it comes
/// from the Glasses sheet rather than the Item sheet, is applied through Glamourer's "bonus item" call, and never belongs
/// to a set.
/// </summary>
public enum GearSlot { Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Ring, Facewear }

/// <summary>One equippable item that is NOT part of a named set on the Outfit sets tab.</summary>
public sealed record GearItem(uint ItemId, string Name, uint Icon, GearSlot Slot, byte Level, ulong Model, string JobText)
{
    /// <summary>The model's set id (low 16 bits of <c>Item.ModelMain</c>).</summary>
    public ushort ModelSet => (ushort)(Model & 0xFFFF);

    public bool IsAccessory => Slot is GearSlot.Ears or GearSlot.Neck or GearSlot.Wrists or GearSlot.Ring;
}

/// <summary>A set recovered from item names (and model ids): pieces are ordered head to ring, at most one per slot.</summary>
public sealed class GearSet
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<GearItem> Items { get; init; } = Array.Empty<GearItem>();

    /// <summary>The set's display icon: its body piece if it has one, otherwise its first piece.</summary>
    public uint Icon { get; init; }

    /// <summary>Lower-cased name + every piece name, so the filter also finds sets by a piece's name.</summary>
    public string SearchText { get; init; } = "";
}

/// <summary>
/// One tile in the per-slot Pieces grid. Role variants of the same gear ("Necklace of Fending" / "of Casting" / ...) share
/// one model and one icon, so they collapse into a single look; clicking applies the representative (the result is identical).
/// </summary>
public sealed class GearLook
{
    public GearItem Representative { get; init; } = null!;
    public IReadOnlyList<GearItem> Variants { get; init; } = Array.Empty<GearItem>();
    public string SearchText { get; init; } = "";
}

/// <summary>
/// Recovers "sets" from loose gear by name, because the game only lists a fraction of its armour as named sets.
///
/// Rule 1 (role gear): most dungeon/crafted/relic armour is named "&lt;Stem&gt; &lt;Noun&gt; of &lt;Role&gt;" (Fending, Maiming,
/// Striking, Scouting, Aiming, Casting, Healing, Slaying, Crafting, Gathering), optionally followed by a "+N" upgrade tier.
/// Pieces of one set share the stem and the role. Two tricky cases are handled explicitly:
///   - multi-word nouns ("Dress Gloves", "Chain Hose") - a lone piece retries with its last two words dropped and joins an
///     existing multi-slot set with that stem;
///   - several tiers sharing one stem ("Titanium ... of Fending" at level 54 and 56) - a bucket with two pieces in one slot is
///     split by item level, then by model set id, until every set has at most one piece per slot.
/// Rule 2 (role-less gear): a stem alone is unsafe ("Hempen" covers dozens of unrelated items), but the same stem AND the same
/// model set id across three or more slots is a reliable outfit ("Amon's Hat / Coat / Sleeves / Breeches / Boots").
/// Anything that fits neither rule stays a loose piece.
/// </summary>
public static class GearGrouper
{
    // The role words the game uses today. They are always accepted; see LearnedRoleMinItems for how new ones are picked up.
    private static readonly HashSet<string> KnownRoles = new(StringComparer.Ordinal)
    {
        "Fending", "Maiming", "Striking", "Scouting", "Aiming", "Casting", "Healing", "Slaying", "Crafting", "Gathering",
    };

    /// <summary>
    /// A trailing " of &lt;Word&gt;" that is not a known role still counts as one when at least this many items carry it.
    /// Ten or more items can only be a naming convention, so a future expansion's new role suffix is picked up by itself,
    /// with no plugin update. (Today nothing else comes close: the next most common word is on five items.)
    /// </summary>
    private const int LearnedRoleMinItems = 10;

    private static readonly Regex RoleTail = new(
        @"^(?<base>.+) of (?<role>[A-Z][a-z]+)(?: (?<tier>\+\d))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyOfTail = new(
        @" of [A-Z][a-z]+( \+\d)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int ModelRuleMinSlots = 3;

    private sealed record Parsed(GearItem Item, string[] Tokens, string Role, string Tier);

    private sealed record Group(string Name, List<GearItem> Items);

    public static List<GearSet> BuildSets(IReadOnlyList<GearItem> items)
    {
        // Facewear is never part of a set.
        items = items.Where(i => i.Slot != GearSlot.Facewear).ToList();

        var groups = new List<Group>();
        BuildRoleGroups(items, groups);

        var grouped = new HashSet<uint>(groups.SelectMany(g => g.Items.Select(i => i.ItemId)));
        BuildModelGroups(items, grouped, groups);

        // Deterministic order, then make every display name unique.
        groups.Sort((a, b) =>
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Items.Min(i => i.ItemId).CompareTo(b.Items.Min(i => i.ItemId));
        });
        foreach (var dup in groups.GroupBy(g => g.Name).Where(d => d.Count() > 1).ToList())
        {
            var names = dup.ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var idx = groups.IndexOf(names[i]);
                groups[idx] = names[i] with { Name = $"{names[i].Name} (Lv {names[i].Items.Min(x => x.Level)})" };
            }
        }
        foreach (var dup in groups.GroupBy(g => g.Name).Where(d => d.Count() > 1).ToList())
        {
            var n = 0;
            foreach (var g in dup.OrderBy(g => g.Items.Min(i => i.ItemId)))
            {
                var idx = groups.IndexOf(g);
                groups[idx] = g with { Name = $"{g.Name} #{++n}" };
            }
        }
        groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var sets = new List<GearSet>(groups.Count);
        foreach (var g in groups)
        {
            var ordered = g.Items.OrderBy(i => i.Slot).ToList();
            var rep = ordered.Find(i => i.Slot == GearSlot.Body) ?? ordered[0];
            var haystack = g.Name + "\n" + string.Join('\n', ordered.Select(i => i.Name));
            sets.Add(new GearSet
            {
                Id = sets.Count,
                Name = g.Name,
                Items = ordered,
                Icon = rep.Icon,
                SearchText = haystack.ToLowerInvariant(),
            });
        }
        return sets;
    }

    /// <summary>One tile per distinct look (slot + model + icon) when <paramref name="dedupe"/>, else one tile per item.</summary>
    public static List<GearLook>[] BuildLooks(IReadOnlyList<GearItem> items, bool dedupe)
    {
        var perSlot = new List<GearLook>[Enum.GetValues<GearSlot>().Length];
        for (var s = 0; s < perSlot.Length; s++)
            perSlot[s] = new List<GearLook>();

        if (dedupe)
        {
            foreach (var grp in items.GroupBy(i => (i.Slot, i.Model, i.Icon)))
            {
                var variants = grp.OrderBy(i => i.ItemId).ToList();
                perSlot[(int)grp.Key.Slot].Add(new GearLook
                {
                    Representative = variants[0],
                    Variants = variants,
                    SearchText = string.Join('\n', variants.Select(v => v.Name)).ToLowerInvariant(),
                });
            }
        }
        else
        {
            foreach (var it in items)
                perSlot[(int)it.Slot].Add(new GearLook
                {
                    Representative = it,
                    Variants = new[] { it },
                    SearchText = it.Name.ToLowerInvariant(),
                });
        }

        for (var s = 0; s < perSlot.Length; s++)
        {
            // Facewear keeps the game's own order: each shape is a block of its colour variants ("Oval", "Silver Oval", ...).
            var inSheetOrder = (GearSlot)s == GearSlot.Facewear;
            perSlot[s].Sort((a, b) =>
            {
                if (inSheetOrder)
                    return a.Representative.ItemId.CompareTo(b.Representative.ItemId);
                var c = string.Compare(a.Representative.Name, b.Representative.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Representative.ItemId.CompareTo(b.Representative.ItemId);
            });
        }
        return perSlot;
    }

    // ---- rule 1: "<Stem> <Noun> of <Role> [+N]" ----------------------------------------------------------------------

    private static void BuildRoleGroups(IReadOnlyList<GearItem> items, List<Group> groups)
    {
        // Count how many items end in each " of <Word>", so a word the game starts using later is learned as a role.
        var candidates = new List<(GearItem Item, Match Match)>();
        var wordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var m = RoleTail.Match(it.Name);
            if (!m.Success) continue;
            candidates.Add((it, m));
            var word = m.Groups["role"].Value;
            wordCounts[word] = wordCounts.GetValueOrDefault(word) + 1;
        }

        var parsed = new List<Parsed>();
        foreach (var (it, m) in candidates)
        {
            var role = m.Groups["role"].Value;
            if (!KnownRoles.Contains(role) && wordCounts[role] < LearnedRoleMinItems) continue;
            var tokens = m.Groups["base"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;                          // need a stem AND a noun
            parsed.Add(new Parsed(it, tokens, role, m.Groups["tier"].Value));
        }

        // Pass 1: stem = every word but the last (the noun).
        var buckets = new Dictionary<(string Stem, string Role, string Tier), List<Parsed>>();
        foreach (var p in parsed)
        {
            var key = (string.Join(' ', p.Tokens[..^1]), p.Role, p.Tier);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Parsed>();
            list.Add(p);
        }

        // Pass 2: a lone piece probably has a multi-word noun; retry with the last two words dropped, but only join a
        // bucket that already looks like a real multi-slot set.
        foreach (var kv in buckets.Where(b => b.Value.Count == 1).ToList())
        {
            var p = kv.Value[0];
            if (p.Tokens.Length < 3) continue;
            var key2 = (string.Join(' ', p.Tokens[..^2]), p.Role, p.Tier);
            if (!buckets.TryGetValue(key2, out var target)) continue;
            if (target.Select(t => t.Item.Slot).Distinct().Count() < 2) continue;
            target.Add(p);
            buckets.Remove(kv.Key);
        }

        foreach (var kv in buckets)
        {
            var (stem, role, tier) = kv.Key;

            // Two pieces in one slot = several tiers/sets share this stem. Split by item level, then by model set id.
            var parts = new List<List<Parsed>> { kv.Value };
            if (HasSlotConflict(kv.Value))
            {
                parts = kv.Value.GroupBy(p => p.Item.Level).Select(g => g.ToList()).ToList();
                var refined = new List<List<Parsed>>();
                foreach (var part in parts)
                {
                    if (!HasSlotConflict(part)) refined.Add(part);
                    else refined.AddRange(part.GroupBy(p => p.Item.ModelSet).Select(g => g.ToList()));
                }
                parts = refined;
            }

            var name = stem + " of " + role + (tier.Length > 0 ? " " + tier : "");
            foreach (var part in parts)
            {
                // Only multi-slot groups are sets, and never with two pieces in one slot (ambiguous: leave them loose).
                if (HasSlotConflict(part)) continue;
                if (part.Select(p => p.Item.Slot).Distinct().Count() < 2) continue;
                groups.Add(new Group(name, part.Select(p => p.Item).ToList()));
            }
        }
    }

    // ---- rule 2: same stem + same model set id, role-less ------------------------------------------------------------

    private static void BuildModelGroups(IReadOnlyList<GearItem> items, HashSet<uint> alreadyGrouped, List<Group> groups)
    {
        var buckets = new Dictionary<(string Stem, ushort Model), List<GearItem>>();
        foreach (var it in items)
        {
            if (alreadyGrouped.Contains(it.ItemId)) continue;
            if (AnyOfTail.IsMatch(it.Name)) continue;                 // role-style name that rule 1 declined
            if (it.Name.EndsWith(')')) continue;                      // "(Red)" colour variants of dated gear
            var tokens = it.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var key = (string.Join(' ', tokens[..^1]), it.ModelSet);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<GearItem>();
            list.Add(it);
        }

        foreach (var kv in buckets)
        {
            var list = kv.Value;
            if (list.Select(i => i.Slot).Distinct().Count() < ModelRuleMinSlots) continue;
            if (list.GroupBy(i => i.Slot).Any(g => g.Count() > 1)) continue;
            groups.Add(new Group(kv.Key.Stem, list));
        }
    }

    private static bool HasSlotConflict(List<Parsed> list)
    {
        var seen = new HashSet<GearSlot>();
        foreach (var p in list)
            if (!seen.Add(p.Item.Slot)) return true;
        return false;
    }
}
