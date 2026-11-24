using System;
using System.Collections.Generic;
using System.Linq;

namespace HOutfits;

/// <summary>
/// One look of an NPC-only face: the face number plus the values that shape it (facial features, eyebrows, eye shape, nose,
/// jaw, mouth), and the NPCs who wear exactly that. <see cref="Wearers"/> is best first: named characters ahead of generic
/// ones ("fallen mage"), one entry per name. The first wearer's customize bytes are what gets applied.
/// </summary>
public sealed record FaceLook(IReadOnlyList<NpcEntry> Wearers, string SearchText)
{
    public NpcEntry Best => Wearers[0];
}

/// <summary>
/// One NPC-only face number on one body, with its looks. The body is race and sex, plus the clan for Hyur only (Midlander and
/// Highlander are separate models; every other race's two clans share one). <see cref="Clan"/> is 0 when the clan doesn't
/// matter.
/// </summary>
public sealed record FaceGroup(
    byte Race,
    byte Clan,
    bool IsFemale,
    byte Face,
    string RaceName,
    string ClanName,
    IReadOnlyList<FaceLook> Looks,
    string SearchText);

/// <summary>
/// The Faces tab's lists, built from the NPC list. Free of game and Dalamud types so it can be checked offline.
///
/// A face number is a face only for one body: face 218 is Hythlodaeus on an Elezen man, 2B on a Hyur woman. Character
/// creation's faces (1 to 8, and 101 to 104) are left out of the Looks list: everyone can already pick those. Only adult
/// bodies (body type 1) count.
///
/// Customize bytes (the 26-byte array): 0 race, 1 sex, 2 body type, 4 clan, 5 face, 12 facial features (seven toggles and the
/// legacy tattoo), 14 eyebrows, 16 eye shape (top bit: small iris), 17 nose, 18 jaw, 19 mouth (top bit: lipstick), 24 face
/// paint (top bit: reversed), 25 face paint colour.
/// </summary>
public static class FaceIndex
{
    private const byte Hyur = 1;

    public static bool IsNpcFace(byte face)
        => face > 8 && face is < 101 or > 104;

    /// <summary>The clan as far as the face's body goes: Hyur keep theirs, every other race counts as 0.</summary>
    public static byte BodyClan(byte race, byte clan)
        => race == Hyur ? clan : (byte)0;

    public static bool Fits(FaceGroup face, byte race, byte clan, bool female)
        => face.Race == race && face.IsFemale == female && face.Clan == BodyClan(race, clan);

    private static bool Adult(NpcEntry n)
        => n.Customize.Length >= 26 && n.Customize[2] == 1 && n.Customize[0] != 0;

    /// <summary>Named characters first ("Hythlodaeus" before "fallen mage"), then by name.</summary>
    public static IEnumerable<NpcEntry> BestFirst(IEnumerable<NpcEntry> npcs)
        => npcs.GroupBy(n => n.Name)
               .Select(g => g.OrderBy(e => e.RowId).First())
               .OrderBy(n => n.Name.Length > 0 && char.IsUpper(n.Name[0]) ? 0 : 1)
               .ThenBy(n => n.Name, StringComparer.Ordinal);

    /// <summary>The bytes that make a look: facial features, eyebrows, eye shape (with small iris), nose, jaw, mouth.</summary>
    private static (byte, byte, byte, byte, byte, byte) LookKey(byte[] c)
        => (c[12], c[14], c[16], c[17], c[18], (byte)(c[19] & 0x7F));

    public static IReadOnlyList<FaceGroup> Build(IReadOnlyList<NpcEntry> npcs)
    {
        var result = new List<FaceGroup>();
        foreach (var g in npcs
                     .Where(n => Adult(n) && IsNpcFace(n.Customize[5]))
                     .GroupBy(n => (Race: n.Customize[0], Female: n.Customize[1] == 1, Clan: BodyClan(n.Customize[0], n.Customize[4]),
                         Face: n.Customize[5])))
        {
            var looks = g.GroupBy(n => LookKey(n.Customize))
                         .Select(l =>
                         {
                             var wearers = BestFirst(l).ToList();
                             return new FaceLook(wearers, string.Join("\n", wearers.Select(w => w.Name)).ToLowerInvariant());
                         })
                         .OrderBy(l => char.IsUpper(l.Best.Name[0]) ? 0 : 1)
                         .ThenBy(l => l.Best.Name, StringComparer.Ordinal)
                         .ToList();
            var first    = looks[0].Best;
            var clanName = g.Key.Clan != 0 ? first.ClanName : string.Empty;
            var search = string.Join("\n", looks.Select(l => l.SearchText)
                                                .Append(first.RaceName.ToLowerInvariant()).Append(clanName.ToLowerInvariant())
                                                .Append(g.Key.Face.ToString()));
            result.Add(new FaceGroup(g.Key.Race, g.Key.Clan, g.Key.Female, g.Key.Face, first.RaceName, clanName, looks, search));
        }

        result.Sort((a, b) =>
        {
            var c = a.Race.CompareTo(b.Race);
            if (c == 0) c = a.Clan.CompareTo(b.Clan);
            if (c == 0) c = a.IsFemale.CompareTo(b.IsFemale);
            return c != 0 ? c : a.Face.CompareTo(b.Face);
        });
        return result;
    }

    /// <summary>
    /// The Parts view: for a body and face (the ones you have now), every value of each part that an NPC with the same body and
    /// face wears, with who wears it. Face paint is drawn over any face, so it is gathered from the whole body, not just the face.
    /// Your own values are left out (there is nothing to apply).
    /// </summary>
    public static IReadOnlyList<FacePart> Parts(IReadOnlyList<NpcEntry> npcs, byte[] mine)
    {
        var race = mine[0];
        var female = mine[1] == 1;
        var clan = BodyClan(race, mine[4]);
        var body = npcs.Where(n => Adult(n) && n.Customize[0] == race && (n.Customize[1] == 1) == female
                                   && BodyClan(n.Customize[0], n.Customize[4]) == clan)
                       .ToList();
        var sameFace = body.Where(n => n.Customize[5] == mine[5]).ToList();

        var result = new List<FacePart>();
        foreach (var kind in PartKinds)
        {
            var source = kind.Kind == PartKind.FacePaint ? body : sameFace;
            foreach (var g in source.GroupBy(n => kind.Value(n.Customize)))
            {
                if (g.Key == kind.Value(mine) || (kind.Kind == PartKind.FacePaint && g.Key.Item1 == 0))
                    continue;
                var wearers = BestFirst(g).ToList();
                result.Add(new FacePart(kind.Kind, g.Key.Item1, g.Key.Item2, wearers));
            }
        }

        result.Sort((a, b) =>
        {
            var c = a.Kind.CompareTo(b.Kind);
            if (c == 0) c = a.Value.CompareTo(b.Value);
            return c != 0 ? c : a.Extra.CompareTo(b.Extra);
        });
        return result;
    }

    private static readonly (PartKind Kind, Func<byte[], (byte, byte)> Value)[] PartKinds =
    {
        (PartKind.Eyebrows,       c => (c[14], 0)),
        (PartKind.EyeShape,       c => ((byte)(c[16] & 0x7F), 0)),
        (PartKind.Nose,           c => (c[17], 0)),
        (PartKind.Jaw,            c => (c[18], 0)),
        (PartKind.Mouth,          c => ((byte)(c[19] & 0x7F), 0)),
        (PartKind.FacialFeatures, c => (c[12], 0)),
        (PartKind.FacePaint,      c => (c[24], c[25])),   // paint (with its reversed bit) and colour go together
    };
}

public enum PartKind { Eyebrows, EyeShape, Nose, Jaw, Mouth, FacialFeatures, FacePaint }

/// <summary>One value of one part that NPCs wear; <see cref="Extra"/> is the colour for face paint, else 0.</summary>
public sealed record FacePart(PartKind Kind, byte Value, byte Extra, IReadOnlyList<NpcEntry> Wearers);
