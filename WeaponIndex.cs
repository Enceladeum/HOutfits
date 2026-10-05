using System;
using System.Collections.Generic;
using System.Linq;

namespace HOutfits;

/// <summary>A class or job as the Weapons tab sees it: one row of the ClassJob sheet.</summary>
/// <param name="JobId">The ClassJob row id (what the player's current class is reported as).</param>
/// <param name="Abbreviation">Shown on the chip, in the game's language.</param>
/// <param name="Priority">The game's own UI ordering: tanks, healers, melee, ranged, casters, crafters, gatherers.</param>
/// <param name="ParentId">The base class this job grew out of (its own id when it has none).</param>
public sealed record ClassInfo(uint JobId, string Abbreviation, string Name, int Priority, uint ParentId);

/// <summary>One class's weapons, split by hand. "All" has one tile per item; the others one tile per distinct look.</summary>
public sealed class ClassWeapons
{
    public IReadOnlyList<GearLook> MainHand { get; init; } = Array.Empty<GearLook>();
    public IReadOnlyList<GearLook> OffHand { get; init; } = Array.Empty<GearLook>();
    public IReadOnlyList<GearLook> AllMainHand { get; init; } = Array.Empty<GearLook>();
    public IReadOnlyList<GearLook> AllOffHand { get; init; } = Array.Empty<GearLook>();
}

/// <summary>
/// The Weapons tab's data: for every class or job, the weapons it can use. Which classes can use a weapon comes straight
/// from the game (the weapon's ClassJobCategory), so the lists are exactly "the weapons of that class": a Paladin gets swords
/// and shields, a White Mage staves, canes and shields, a Carpenter their primary and secondary tools.
///
/// Pure (no game or Dalamud types), like <see cref="GearGrouper"/>, so it can be checked against the real data offline.
/// </summary>
public sealed class WeaponIndex
{
    /// <summary>The chips, in the game's order. Base classes whose job covers them (Gladiator, because of Paladin) are left out.</summary>
    public IReadOnlyList<ClassInfo> Classes { get; init; } = Array.Empty<ClassInfo>();

    /// <summary>Every class that has weapons, including the ones with no chip (so a player who is still a Gladiator is covered).</summary>
    public IReadOnlyDictionary<uint, ClassWeapons> ByClass { get; init; } = new Dictionary<uint, ClassWeapons>();

    /// <summary>Every class that has weapons, by id, for labels.</summary>
    public IReadOnlyDictionary<uint, ClassInfo> Info { get; init; } = new Dictionary<uint, ClassInfo>();

    /// <summary>
    /// The class ids to show as chips for a player on <paramref name="active"/>: their own class first, even when it has no chip
    /// of its own (a Gladiator is otherwise covered by the Paladin chip), then every other chip in the game's order. When there
    /// is no weapon data for the active class (no character loaded, or a job from a patch newer than this list) nothing leads.
    /// </summary>
    public List<uint> ChipOrder(uint active)
    {
        var ids = new List<uint>(Classes.Count + 1);
        if (Info.ContainsKey(active))
            ids.Add(active);
        foreach (var c in Classes)
            if (c.JobId != active)
                ids.Add(c.JobId);
        return ids;
    }

    /// <param name="weapons">Every weapon-slot item.</param>
    /// <param name="classes">Every playable class or job.</param>
    /// <param name="classesByCategory">For each ClassJobCategory row id, the ids of the classes it allows.</param>
    public static WeaponIndex Build(
        IReadOnlyList<GearItem> weapons,
        IReadOnlyList<ClassInfo> classes,
        IReadOnlyDictionary<uint, HashSet<uint>> classesByCategory)
    {
        var byClass = new Dictionary<uint, ClassWeapons>();
        var withWeapons = new List<ClassInfo>();

        foreach (var cls in classes)
        {
            var usable = weapons
                .Where(w => classesByCategory.TryGetValue(w.JobCategory, out var allowed) && allowed.Contains(cls.JobId))
                .ToList();
            if (usable.Count == 0)
                continue;

            var looks = GearGrouper.BuildLooks(usable, dedupe: true);
            var every = GearGrouper.BuildLooks(usable, dedupe: false);
            byClass[cls.JobId] = new ClassWeapons
            {
                MainHand    = looks[(int)GearSlot.MainHand],
                OffHand     = looks[(int)GearSlot.OffHand],
                AllMainHand = every[(int)GearSlot.MainHand],
                AllOffHand  = every[(int)GearSlot.OffHand],
            };
            withWeapons.Add(cls);
        }

        // A base class (Gladiator) is redundant on the chip row when one of its jobs (Paladin) is listed: the job's weapons
        // are a superset of the base class's, so one chip covers both.
        var parents = new HashSet<uint>(withWeapons.Where(c => c.ParentId != c.JobId).Select(c => c.ParentId));
        var chips = withWeapons
            .Where(c => !parents.Contains(c.JobId))
            .OrderBy(c => c.Priority).ThenBy(c => c.JobId)
            .ToList();

        return new WeaponIndex
        {
            Classes = chips,
            ByClass = byClass,
            Info    = withWeapons.ToDictionary(c => c.JobId),
        };
    }
}
