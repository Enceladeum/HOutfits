using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HOutfits;

/// <summary>
/// How many choices character creation offers for the list-style face parts (eyebrows, eye shape, nose, jaw, mouth), per clan
/// and sex. A value at or above the count is one only NPCs have; the Faces tab marks those. Read the way Glamourer reads them:
/// CharaMakeType row (clan - 1) * 2 + sex, the menu whose Customize is the part's byte index, its SubMenuNum. List values
/// start at 0, so creation's values are 0 to count - 1.
/// </summary>
public sealed class CreationLimits
{
    public static readonly CreationLimits Empty = new(new Dictionary<(byte, int), int>());

    private static readonly int[] ListBytes = { 14, 16, 17, 18, 19 };   // eyebrows, eye shape, nose, jaw, mouth

    private readonly Dictionary<(byte Row, int Byte), int> _counts;

    private CreationLimits(Dictionary<(byte, int), int> counts) => _counts = counts;

    /// <summary>True when the value (already masked) is beyond what character creation offers this clan and sex.</summary>
    public bool IsNpcOnly(byte clan, bool female, int byteIndex, byte value)
        => clan > 0 && _counts.TryGetValue(((byte)((clan - 1) * 2 + (female ? 1 : 0)), byteIndex), out var count) && value >= count;

    public static CreationLimits Load(IDataManager data, IPluginLog log)
    {
        try
        {
            var counts = new Dictionary<(byte, int), int>();
            foreach (var row in data.GetExcelSheet<CharaMakeType>())
            {
                if (row.RowId > byte.MaxValue)
                    continue;
                foreach (var menu in row.CharaMakeStruct)
                    if (Array.IndexOf(ListBytes, (int)menu.Customize) >= 0 && menu.SubMenuNum > 0)
                        counts.TryAdd(((byte)row.RowId, (int)menu.Customize), menu.SubMenuNum);
            }
            return new CreationLimits(counts);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Couldn't read the character creation menus; NPC-only face parts won't be marked.");
            return Empty;
        }
    }
}
