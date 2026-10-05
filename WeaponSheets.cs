using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace HOutfits;

/// <summary>
/// Reads the two tables the Weapons tab needs out of the game's sheets: which classes and jobs exist, and which of them each
/// ClassJobCategory row allows (every weapon names one). Lumina only, no Dalamud types, so it can be checked against the real
/// sheets offline, like <see cref="GearGrouper"/> and <see cref="WeaponIndex"/>.
/// </summary>
public static class WeaponSheets
{
    /// <summary>
    /// ClassJobCategory has one yes/no column per class or job, named by its ENGLISH abbreviation ("PLD", "WHM", ...). Looked
    /// up by name, so a column the game adds later is simply not known yet rather than misread, and one it drops is skipped.
    /// </summary>
    private static readonly Dictionary<string, PropertyInfo> CategoryColumns = typeof(ClassJobCategory)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(bool))
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// The hand a weapon-slot item goes in, or null when it isn't a weapon. No item fits both hands (two-handed weapons
    /// are 1 for the main hand and -1, "blocked", for the off hand), so the first match is the answer.
    /// </summary>
    public static GearSlot? WeaponSlotOf(EquipSlotCategory c)
    {
        if (c.MainHand == 1) return GearSlot.MainHand;
        if (c.OffHand == 1) return GearSlot.OffHand;
        return null;
    }

    /// <summary>
    /// Every class or job the category table has a column for, and for each of <paramref name="categoryIds"/> the ids of the
    /// classes that category allows. <paramref name="jobsEnglish"/> supplies the English abbreviations the columns are named
    /// by (the client-language sheet is used when it is null, which is right on an English client).
    /// </summary>
    public static (List<ClassInfo> Classes, Dictionary<uint, HashSet<uint>> ByCategory) ReadClasses(
        ExcelSheet<ClassJob> jobs,
        ExcelSheet<ClassJob>? jobsEnglish,
        ExcelSheet<ClassJobCategory> categories,
        IEnumerable<uint> categoryIds)
    {
        var classes = new List<ClassInfo>();
        var column  = new Dictionary<uint, PropertyInfo>();          // class id -> its yes/no column in ClassJobCategory
        foreach (var j in jobs)
        {
            if (j.RowId == 0) continue;
            var english = (jobsEnglish ?? jobs).GetRowOrDefault(j.RowId)?.Abbreviation.ToString().Trim() ?? string.Empty;
            if (!CategoryColumns.TryGetValue(english, out var prop)) continue;

            var abbreviation = j.Abbreviation.ToString().Trim();
            classes.Add(new ClassInfo(
                j.RowId,
                abbreviation.Length > 0 ? abbreviation : english,
                TitleCase(j.Name.ToString().Trim()),
                j.UIPriority,
                j.ClassJobParent.RowId));
            column[j.RowId] = prop;
        }

        var byCategory = new Dictionary<uint, HashSet<uint>>();
        foreach (var id in categoryIds.Distinct())
        {
            if (categories.GetRowOrDefault(id) is not { } row) continue;
            object boxed = row;                                      // the columns are read by reflection
            var allowed = new HashSet<uint>();
            foreach (var (jobId, prop) in column)
                if (prop.GetValue(boxed) is true)
                    allowed.Add(jobId);
            byCategory[id] = allowed;
        }
        return (classes, byCategory);
    }

    // The sheet keeps class names lower-case ("white mage"); the tooltip reads better as a name.
    private static string TitleCase(string name)
        => name.Length == 0 ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
}
