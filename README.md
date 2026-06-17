# HOutfits

Apply a complete FFXIV outfit set to **yourself** in one click, routed through
[Glamourer](https://github.com/Ottermandias/Glamourer) — instead of selecting
each piece from Glamourer's dropdown.

It reads the game's named outfit sets (the `MirageStoreSetItem` sheet — the same
data behind the in-game fitting room's outfit list): set icon, set name, and the
component pieces. The difference from the fitting room is where the click goes —
HOutfits applies the set through Glamourer, as an appearance, rather than a
temporary try-on.

Open with `/houtfits`, filter, and:

- **Click a set name** → the whole set is applied to you.
- **Click a single piece icon** → just that piece is added (your other slots are
  left as they are — handy to grab only the gloves from a set).
- **Filter by set *or* item name** — typing `ushanka` finds the Imperial sets
  that include the Ushanka head piece, even though "Ushanka" isn't in the set's
  own name.

**Scope:** this is a convenience for applying *complete, named sets* (and their
individual pieces). Loose gear that isn't part of a named `MirageStoreSetItem`
set isn't listed — for arbitrary single items, use Glamourer directly.

## Installing

### From the custom repo (recommended)

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin
   Repositories**.
2. Paste this URL into a new row, click the **+**, then **Save**:

   ```
   https://raw.githubusercontent.com/Enceladeum/HOutfits/main/repo.json
   ```
3. Open the plugin installer (`/xlplugins`), search for **HOutfits**, and click
   **Install**.

Requires [Glamourer](https://github.com/Ottermandias/Glamourer) to be installed
and enabled.

### Local dev build

To run a build you made yourself:

1. Build it (see **Building** below). The output folder ends up with
   `HOutfits.dll`, a generated `HOutfits.json` manifest, and `Glamourer.Api.dll`.
2. In game, `/xlsettings` → **Experimental** → **Dev Plugin Locations**. Add the
   path to the built `HOutfits.dll` (or its folder), save, and hit the reload/
   scan button.
3. **HOutfits** appears in **Installed Dev Plugins**; enable it.

> If you previously registered an older build under a different name
> (e.g. "OutfitGlamourer"), remove that stale Dev Plugin Location first — the
> rename makes this a different plugin to Dalamud, and a leftover entry can
> shadow the new one.

## How it works (and why it's shaped this way)

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

**Apply behaviour.** `GlamourerIpc.ApplyItem` calls Glamourer's `SetItem` with
`ApplyFlag.Equipment`, no `Once`, `key = 0`. Verified against Glamourer's source:

- no `Once` → `StateSource.IpcFixed` → the change **sticks** across redraws,
  matching a manual dropdown edit in Glamourer's own UI (not the transient
  try-on path);
- `key = 0` → no lock, so you can still edit any slot by hand afterward;
- dye is left untouched.

A whole-set apply is one `SetItem` per piece; a single-piece click is one
`SetItem` for that one slot (additive). The status line reports results, with
failures logged to `/xllog`.

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
- `GlamourerIpc.cs` — the three IPC calls (version check, `SetItem`, revert).
- `MainWindow.cs` — the ripped-down table UI.
- `HOutfits.csproj` — project + manifest metadata (no separate `.json`).

## Caveats

- An item only applies if it's a valid glamour target for your character in
  Glamourer's eyes; non-human actors return `ActorNotHuman`.
- This sets *appearance*, like try-on — it does not unlock, acquire, or move any
  item. You're not wearing it "for real," same as a glamour.
- Sigs/IPC labels drift across Glamourer versions; if applies silently no-op
  after a Glamourer update, check `Available` and bump the `Glamourer.Api`
  package.

## Acknowledgements

This plugin's core idea — *apply a whole outfit set at once* — came from the
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

### Why MIT, given HaselDebug is AGPL-3.0

HaselDebug (and Haselnussbomber's other plugins, and the `HaselCommon` library)
are licensed under **AGPL-3.0**. That's worth addressing directly, because
"inspired by an AGPL project" can look like a licensing problem. It isn't one
here, for a simple reason: **copyright protects expression — actual code — not
ideas, methods, or facts.** What carried over from HaselDebug is the *concept*
("iterate the outfit sheet, apply each piece"), which copyright does not cover.
None of HaselDebug's *code* was copied, and the `HaselCommon` AGPL library is not
referenced or bundled (the project depends only on the Dalamud SDK and
`Glamourer.Api`). The game data is Square Enix's; the apply path is Glamourer's
API. With no AGPL-licensed expression incorporated, there is no derivative work,
and the AGPL's copyleft terms do not extend to this plugin — so it is free to be
licensed under MIT.

This note is informational, not legal advice. If you fork this and pull in
`HaselCommon` or copy AGPL-licensed code from any of these projects, that changes
the analysis and your fork would need to honour the AGPL.
