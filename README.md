# HOutfits

Apply a complete FFXIV player character outfits or NPC appearances and gear set to **yourself** in one click, routed through
[Glamourer](https://github.com/Ottermandias/Glamourer) — instead of selecting
each piece from Glamourer's dropdown: HOutfits applies the set through Glamourer. 
Will write NPC names to your name plate on toggle if HMoniker is installed.

Open with `/houtfits`, filter, and:

- **Click a set name** → the whole set is applied to you.
- **"Include accessories" toggle** at the top of the window: when off, applying a
  whole set skips earrings, necklace, bracelets, and rings, so a set drops onto
  your glamour without disturbing accessories you keep hidden or glamoured
  separately. The choice persists across sessions, and you can still click a
  single accessory icon to apply just that piece. (Excluded slots show dimmed in
  the set's piece row.)
- **Click a single piece icon** → just that piece is added (your other slots are
  left as they are — handy to grab only the gloves from a set).
- **Filter by set *or* item name** — typing `ushanka` finds the Imperial sets
  that include the Ushanka head piece, even though "Ushanka" isn't in the set's
  own name.
- **Hover any piece icon** → a large preview of it, so you can see what you're about
  to put on before you apply it (Glamourer itself only shows an icon for the piece you
  already have selected).
- **Name-grouped sets.** The game only lists a fraction of its armour as named sets.
  HOutfits also recognises gear that is obviously a set from its item names (for
  example *Allagan Visor of Striking*, *Allagan Cuirass of Striking*, ... become
  **Allagan of Striking**) and lists it with the rest, so it applies in one click too.
  Untick **Include name-grouped sets** to see only the game's own sets.
- **Loose gear tab.** Every wearable piece that belongs to no set at all, browsed by
  slot as an icon grid: hover for a large preview, click to apply just that piece.
  Pieces that are the same gear sold once per role ("of Fending", "of Casting", ...)
  look identical, so **Hide duplicate looks** collapses them into one tile. A **Facewear**
  chip lists glasses and the like, which Glamourer treats as a slot of their own.

**Scope:** armour, accessories and facewear. Complete sets (the game's own and the ones
recovered by name) apply in one click; anything that belongs to no set is on the
**Loose gear** tab. Weapons aren't covered.

## Installing

### From the custom repo (recommended)

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin
   Repositories**.
2. Paste this URL into a new row, click the **+**, then **Save**:

   ```
   https://raw.githubusercontent.com/Enceladeum/DalamudPlugins/main/repo.json
   ```

   This one URL is The Enceladeum's shared feed: it lists **every** plugin by this
   author (and any added later), so there's nothing else to track down.
3. Open the plugin installer (`/xlplugins`), search for **HOutfits**, and click
   **Install**.

### Local dev build

To run a build you made yourself:

1. Build it (see **Building** below). The output folder ends up with
   `HOutfits.dll`, a generated `HOutfits.json` manifest, and `Glamourer.Api.dll`.
2. In game, `/xlsettings` → **Experimental** → **Dev Plugin Locations**. Add the
   path to the built `HOutfits.dll` (or its folder), save, and hit the reload/
   scan button.
3. **HOutfits** appears in **Installed Dev Plugins**; enable it.

## How it works

**Data.** `OutfitService` walks the `MirageStoreSetItem` sheet. That sheet has no
name or category columns — only eleven per-slot link columns (`Head`, `Body`,
`Hands`, `Legs`, `Feet`, `Earrings`, `Necklace`, `Bracelets`, `Ring`, plus
`MainHand`/`OffHand`), each a `RowRef<Item>` for that slot. The **set name and
icon** come from the `Item` whose id equals the set row's own `RowId` (a set row
at id N corresponds to Item N, e.g. "Imperial Attire of Fending"); the chest (or
first) piece is used only as a fallback if that item doesn't resolve.

**Slot resolution is free.** Because each column *names* its slot, we map column
→ `ApiEquipSlot` directly (`Body` → `Body`, `Earrings` → `Ears`, `Bracelets` →
`Wrists`, `Ring` → `RFinger`). No `EquipSlotCategory` lookup is needed. Weapons
are skipped (`_includeWeapons = false`) so applying an outfit never changes your
weapon.

**Search.** Each row carries a pre-lowercased haystack of its set name plus every
piece name, so the filter is a cheap substring match that covers piece names too.

**Name-grouped sets.** Most of the game's armour is named `<Stem> <Noun> of <Role>`
(Fending, Maiming, Striking, Scouting, Aiming, Casting, Healing, Slaying, Crafting,
Gathering), optionally with a `+N` upgrade tier, and the pieces of one set share the
stem and the role. A role word the game introduces later is picked up automatically once
ten or more items use it. `GearGrouper` groups the gear that isn't in any game set on that
basis. Multi-word nouns ("Dress Gloves") join the set that matches their shorter stem,
and when several tiers share a stem ("Titanium ... of Fending" at level 54 and 56) they
are split by item level, then by model, so a set never has two pieces in one slot; such
sets are labelled with their level, e.g. `Titanium of Fending (Lv 54)`. Gear without a
role suffix is only grouped when three or more pieces share both the name stem and the
same model set id ("Amon's Hat / Coat / Sleeves / Breeches / Boots"). Anything else stays
loose. The scan runs once, on a background thread, the first time the window is opened.

The rules read the English item names whatever language the game runs in (looked up by item
id), so every client finds the same sets. Piece names are still shown in your language and
are searchable in either language; the labels of the recovered sets stay in English, and on
non-English clients they are listed after the game's own sets.

**Loose gear.** The pieces left over after grouping, by slot. A slot is the one column of
the item's `EquipSlotCategory` set to 1. Items whose slot, model and icon are identical
are the same look; they collapse into one tile that applies the lowest-numbered variant.

Facewear isn't in the Item sheet. It comes from the `Glasses` sheet (one row per shape and
colour; the row id is the id Glamourer knows it by) and is applied through Glamourer's
`SetBonusItem` call, so it needs a Glamourer recent enough to have bonus items. It keeps the
game's own order, so each shape's colour variants stay together. Facewear is never part of a set.

**Apply behaviour.** `GlamourerIpc.ApplyItem` calls Glamourer's `SetItem` with
`ApplyFlag.Equipment`, no `Once`, `key = 0`. Verified against Glamourer's source:

- no `Once` → `StateSource.IpcFixed` → the change **sticks** across redraws,
  matching a manual dropdown edit in Glamourer's own UI (not the transient
  try-on path);
- `key = 0` → no lock, so you can still edit any slot by hand afterward;
- dye is left untouched.

A whole-set apply is one `SetItem` per piece; a single-piece click is one
`SetItem` for that one slot (additive). With **Include accessories** off, the
whole-set path skips the accessory slots and sends only head, body, hands, legs,
and feet; an individual accessory click still applies that piece regardless. The
status line reports results, with failures logged to `/xllog`.

**Target.** Always the local player (`objectIndex = 0`). Glamourer's IPC does
not expose its own currently-selected actor, so "apply to whoever's selected in
Glamourer" isn't reachable from a separate plugin; self-only keeps it simple. The
same `SetItem` path works on any object index, which is the hook for applying to
other actors later.

**Threading.** A click only queues the work (`_pendingSet` / `_pendingPiece`);
the IPC fires at the top of the next `Draw`, keeping draw callbacks
side-effect-light per the Dalamud pattern.

## Building

```
dotnet build -c Release
```

A plain `dotnet build` (Debug) also produces a loadable plugin — the SDK
generates the manifest in both configurations. If you ever rename or change
references and get odd load behavior, delete `bin/` and `obj/` (or `dotnet
clean`) and rebuild; MSBuild caches aggressively and a stale `obj/` can keep an
old assembly name around.

Requires the Dalamud dev environment (the `Dalamud.NET.Sdk` resolves the game
references) and restores the **`Glamourer.Api`** NuGet package, which provides
`Glamourer.Api.IpcSubscribers.*` and `Glamourer.Api.Enums.*`. Pin the
`Glamourer.Api` version in the `.csproj` to match the Glamourer build you target
(the `SetItem.V3` signature used here). `Glamourer.Api.dll` **ships inside
`latest.zip`** next to `HOutfits.dll`, and must stay there. Each Dalamud plugin
loads in its own isolated `AssemblyLoadContext`, which does **not** inherit
Glamourer's already-loaded copy — so if `Glamourer.Api.dll` is absent from the
plugin folder the load aborts with `ReflectionTypeLoadException` /
`FileNotFoundException: Glamourer.Api`. Do **not** add `ExcludeAssets="runtime"`;
a plain `PackageReference` copies the DLL to output, which is what you want. The
two copies (ours and Glamourer's) don't conflict — IPC crosses the load-context
boundary by string label with primitive args, never by shared CLR instances.

The `Dalamud.NET.Sdk` version in the `.csproj` must match your installed Dalamud
(15.x here). There is no hand-written manifest `.json`: the SDK generates the
manifest at build time and stamps the API level from the SDK version. An older
SDK stamps an older level and Dalamud flags the plugin "outdated and
incompatible" even though it loads — bump the SDK to fix that, don't hardcode a
level. Manifest metadata (`Name`, `Author`, `Punchline`, `Description`) lives in
the `.csproj` PropertyGroup.

Load `bin/Release/HOutfits.dll` as a dev plugin.

## Files

- `Plugin.cs` — entry point, DI, window system, `/houtfits` command.
- `OutfitService.cs` — reads MirageStoreSetItem's per-slot columns, apply loop.
- `GearService.cs` — the one-off background scan: name-grouped sets and loose gear.
- `GearGrouper.cs` — the name/model grouping rules and the loose-gear tiles (no game or
  Dalamud types, so it can be tested against real data in a plain console app).
- `NpcService.cs`, `NpcStateBuilder.cs`, `BNpcNameData.cs` — the NPCs tab.
- `GlamourerIpc.cs`, `MonikerIpc.cs` — the IPC wrappers.
- `MainWindow.cs` — the tabbed table/grid UI.
- `SlotIconService.cs` — generic slot silhouettes for NPC pieces.
- `Configuration.cs`: persisted settings.
- `HOutfits.csproj` — project + manifest metadata (no separate `.json`).

## Caveats

- An item only applies if it's a valid glamour target for your character in
  Glamourer's eyes; non-human actors return `ActorNotHuman`.
- Sigs/IPC labels drift across Glamourer versions; if applies silently no-op
  after a Glamourer update, check `Available` and bump the `Glamourer.Api`
  package.

## Acknowledgements

This plugin's core idea to *apply a whole outfit set at once* came from the
**Outfits** tab in [HaselDebug](https://github.com/Haselnussbomber/HaselDebug)
by Haselnussbomber, which surfaces the game's `MirageStoreSetItem` data and lets
you try a full set on in the fitting room. Thanks to Haselnussbomber for a great
reference tool; go support their work.

HOutfits is an independent implementation of that idea. It does **not**
contain any HaselDebug code. The set list is read from the game's own
`MirageStoreSetItem` sheet via stock Lumina (the column layout comes from the
community [EXDSchema](https://github.com/xivdev/EXDSchema), i.e. Square Enix's
game data, not HaselDebug); the UI is plain Dalamud ImGui; and instead of the
in-game fitting room, each piece is applied through
[Glamourer](https://github.com/Ottermandias/Glamourer)'s public IPC. No part of
HaselDebug's source — its ImGui drawing, its table framework, its try-on logic,
or the `HaselCommon` library — is used or linked here.

## License

MIT — see [LICENSE](LICENSE). You may use, modify, and redistribute this freely,
including in closed-source projects.
