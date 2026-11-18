# Roadmap

> **Mirrored in `docs/roadmap.md` and `~/.opencode/plan/game_development_plan.md` — update both.**
>
> This file is the status-and-milestones view. The plan document keeps the full
> design spec (biomes, movement maths, construction rules, hardware outline).

## Status snapshot

Anchor: **2026-11-18**, M4.5 true-colour overlay, **148 C# tests + 5 pipeline tests passing**.

| Area | State | Evidence |
|---|---|---|
| Moon data pipeline | **Done** | `data/moon`: 41,162 regions, 14,117,882 fine hexes, 343 hexes/region, 10 biomes, `fine.bin` 54.6 MB; bake fingerprint unchanged |
| Mars data pipeline | **Done (M4)** | `data/mars`: 41,162 regions, 7 biomes, MOLA elevation, USGS geology and Robbins & Hynek craters; `generatedUtc=2026-11-18T19:35:34+00:00` |
| Sim core | **Done** | H3, biomes, movement, routing, world loader, site scoring, per-body building catalogues, local grid, sites, campaign, save store, slot catalogue |
| Godot runtime | **Done** | Globe, region/colony inspectors, per-hex site view with local placement, body selection, system map, save/load, CLI automation |
| Campaign shell | **Done (M2, extended in M4)** | Main menu, multi-slot saves, Continue, manual save, toasts, pause, search, settings, per-body last slots |
| Simulation loop | **Done (M1)** | `Sim/Time` clock + seeded RNG, construction lifecycle, economy, power/life support, tech enforcement, live controls |
| Site view (per-hex grid) | **Done (M3)** | 37-cell radius-3 disk; multiple installations per fine hex; save v3 with v1/v2 migration |
| Multi-body | **Done (M4)** | Moon + Mars, `--body`, system-map travel, body-keyed saves, cross-body refusal, fresh-slot creation |
| Surface imagery | **Done (M4.5)** | Real Moon/Mars colour sidecars in globe/region/colony; `Tab` or `--overlay=biome` falls back; no manifest/save change |
| CI and performance gate | **Done (M4, extended M4.5)** | `.github/workflows/ci.yml`; synthetic tile + imagery bakes, 11 headless Godot smokes, `--frame-budget=20` p95 gate |
| Hardware designer | **Planned (M6)** | Outline only (plan §6.2) |

### What "done" means today

The player can: start or continue a named campaign from the main menu, keep
several isolated save slots, save manually, rotate the active body's globe, search
an H3 index, open a region, inspect hexes across five overlays, plan a rover route,
press `Enter` to open a fine hex as a site, place several installations on its
37-cell local grid, and have all of it persist across launches against a
fingerprinted bake. The player can start on Moon or Mars directly with `--body`
or travel between them with the `M` Solar System Map.

With a valid imagery sidecar the player sees real Moon or Mars colour by default in
the globe, region and colony views; `Tab` (or `--overlay=biome`) switches to the
analytical biome map. Missing or corrupt imagery only produces a warning.

The player can also pass time with pause/1×/2×/4×, watch installations finish
construction, earn/spend credits and stockpile resources, see power or life-support
shortfalls called out in the HUD, and find occupied hexes as aggregate markers in
every region and colony overlay.

The player cannot: pick a tech in the UI, rename a campaign, or play Earth or
another body yet.

### Naming (locked)

- **Colony view** — the region-scale isometric management view that exists today
  (`ManagementView`, `--view=management`, `G` from the region view). It shows all
  343 hexes of one region as prisms. `M` is now the Solar System Map.
- **Site view** — the per-hex local build grid from plan §2.6, shipped in **M3**
  (`SiteView`, `--view=site`, `Enter` from region/colony). It is a different scale,
  not a rename, and is the only place where installations are placed.

## Milestones

Order: **M2 → M1 → M3 → M4 → M5 → M6**. M2 was chosen ahead of M1 deliberately:
the shell is cheap and makes every later demo presentable, while the clock is the
expensive part and needs the design attention M1 deserves.

### M2 — Campaign shell (done)

**Outcome:** the game has a front end and a save-slot model instead of booting
straight into the last region.

**Status:** shipped 2026-11-16 · **103 tests passing** · decisions D15–D16.

Implemented:

- `ShellOverlay` draws the main menu, slot list, settings, pause, delete
  confirmation, region search and toast log with the existing immediate-mode HUD.
- `CampaignSlotStore` (Sim) lists slots, heals `slot.json`, creates/deletes slot
  directories and orders by last played. `CampaignStore` reads headers and
  summaries without opening a campaign.
- `GameSettings` (`user://settings.json`) stores the save root, last slot, relief
  default and FPS flag outside any campaign.
- `CampaignService` is slot-aware, saves with `F5`, reports failures as toasts and
  tracks the building total in O(1).
- `MoonGlobe` draws visited/active markers; `/` opens H3 search and `Home` focuses
  the active region.
- Continue restores mode, region, globe/colony camera and overlay.

Scope (as planned):

1. **Main menu overlay** — New campaign · Continue · Load slot · Settings · Quit.
   Drawn with the existing immediate-mode HUD style; no scene restructure required.
2. **Save-slot service** over `CampaignStore` — enumerate/create/delete slots under
   `user://saves/`, slot summary (name, created, updated, building count, visited
   regions), last-played ordering. M2 itself kept `campaign.json` **v1-compatible**;
   slot summary is a sibling file or optional additive fields, never a breaking
   change. (M1 later introduced v2 with a tested v1 migration.)
3. **Manual save + visible feedback** — `F5` saves; save/load failures become
   on-screen notifications instead of `GD.PushWarning` only.
4. **Notifications/toasts** — small log for save, load, quarantine, placement errors.
5. **Region search and markers** — jump to an H3 index, "go to active region", and
   visited/active markers drawn on the globe.
6. **Settings panel** — save root, default relief exaggeration, keybind reference.
7. **Pause overlay + time controls, scaffolded** — M2 shipped visible but
   disabled buttons; M1 wired them to the live clock.

**Acceptance criteria** — all verified on 2026-11-16:

- [x] New campaign → place a building → quit → **Continue** restores mode, region,
  camera and buildings.
- [x] Two slots stay fully isolated; deleting a slot removes its directory and is
  reflected in the list.
- [x] A corrupt/incompatible slot is shown as such and is never overwritten.
- [x] `--save`, `--fresh`, `--nosave`, `--shot` automation still behave exactly as today.
- [x] No change to the v1 save schema that breaks an existing `campaign.json`
  (legacy `slot1` with 17 buildings loads and gains a `slot.json`).

### M1 — Simulation loop (done)

**Outcome:** time passes and buildings actually get built and run.

**Status:** shipped 2026-11-17 (updated after M3) · **132 tests passing** ·
resolves open questions 4 and 7.

Implemented:

1. **`Sim/Time`** — `SimulationClock` (game hours, 0.25 h fixed step) and
   `DeterministicRandom` (SplitMix64 with an explicit persisted state).
2. **`CampaignState.Tick(hours)`** — engine-free, fixed-step construction,
   production, maintenance and status transitions. Only regions installed in
   memory are ticked; that keeps the globe cheap and is a documented M1 limitation.
3. **Construction lifecycle** — `Planned → UnderConstruction → Operational` from
   `BuildTimeHours` and `ConstructionStartTime`; zero build time is immediately
   operational; `Paused` freezes construction and production.
4. **Economy** — all-or-nothing `Cost` charge at placement (credits + stockpile),
   per-hour maintenance and throughput, aggregated `RegionEconomy` for the HUD.
5. **Power and life support** — `PowerKw < 0` generates; consumers are idled in a
   deterministic category/Id order when supply, stockpile inputs or maintenance
   are short, with a human-readable `StatusReason`.
6. **Tech unlocks** — `RequiredTech` is enforced in `CampaignState.Preview` and
   `TryPlace`, not only `BuildingRegistry.GetAvailableAtSite`; `UnlockTech` and
   `Meta.UnlockedTechs` provide the minimal research state.
7. **Save v2 + migration** — `GameTimeHours`, `SciencePoints`, `RngSeed`,
   `RngState`, `UnlockedTechs`, per-building `Paused` and `StatusReason`; a tested
   v1 → v2 migration grants starting credits, stockpile and a stable RNG seed.
   M3 superseded the shard layout with v3 while keeping this chain;
   unknown/incompatible saves are still refused, never overwritten.
8. **Live time controls** — pause/1×/2×/4× in the pause overlay, `Space` to toggle
   pause, HUD day/hour and economy line, 5-second autosave, `--time` and
   `--advance` automation hooks.
9. **Pentagon neighbour test** — 5 distinct neighbours on a pentagon cell.
10. **Occupied-region markers** — green rings on the globe and status-coloured
    pins with counts in biome, terrain, score and sites overlays of both the
    region and colony views (aggregate markers after M3).

**Acceptance criteria** — verified on 2026-11-17:

- [x] A habitat plus a solar array reach `Operational` after their build times.
- [x] A region with negative power marks consumers `Idle` and the HUD says why.
- [x] Credits decrease by exactly the building cost; an unaffordable placement is refused.
- [x] Save → reload preserves time, progress, status and treasury.
- [x] Deterministic tick tests: same seed + same tick count = same state.
- [x] A v1 fixture loads and migrates under v2.
- [x] Occupied regions stay findable in every globe/region/colony overlay.

### M3 — Site view (done, plan §2.6)

**Outcome:** selecting a fine hex opens a local build grid inside that hex, and a
hex can hold several installations.

**Status:** shipped 2026-11-17 · **132 tests passing** · decisions D20–D22 ·
resolves open questions 3, 6 and 10.

Implemented:

1. **`LocalGrid` and `SiteState` (Sim)** — a radius-3 axial disk of 37 cells,
   local footprints from `FootprintRadius`, and per-site occupancy independent of
   other fine hexes.
2. **Site-scoped campaign API** — `Preview` / `TryPlace` / `TryRemove` /
   `TryGetAt` take a parent hex plus a local cell; `SiteView` clears one site by
   repeated `TryRemove`. Cost, tech and status semantics from M1 are unchanged.
3. **Save v3** — region shards store `sites` → `installations` with `localCenter`.
   v1/v2 shards convert to one site per legacy centre hex at local `(0,0)`;
   footprints stay derived and v1/v2 files are not overwritten on load.
4. **`SiteView`** — a 37-cell isometric grid reusing `IsoProjection`, hover
   ghost, click-to-place, local-cell removal, `C` site clear, `,`/`.` building
   cycling, and `Enter`/`Esc` navigation from region or colony view.
5. **Inspector views** — `RegionView` and `ManagementView` no longer place or
   remove; they draw one aggregate marker per occupied fine hex with count and
   status. All mutations go through `SiteView` → `CampaignState`.
6. **CLI** — `--view=site --hex=N --active=<defId> --place=q,r`, with repeatable
   `--place`; `--place` outside site view is refused with a warning.

> **Art direction (noted 2026-11-16, still open).** Buildings are **3D models**
> in a **fixed 2.5D isometric view**; the camera does not freely orbit. M3 kept
> procedural prisms and did not integrate assets. Questions 11–12 remain open.

**Acceptance criteria** — verified 2026-11-17:

- [x] Two `air_reclaimer` installations on one hex's grid are saved and restored
  at local `(0,0)` and `(1,0)` with progress/status preserved.
- [x] A v2 shard converts to v3 without losing credits, time, statuses or idle
  reasons; v1 → v2 → v3 stays covered by tests.
- [x] Region and colony views show one aggregate marker per occupied fine hex.
- [x] `--view=site` smoke places installations, advances 24 h, reloads and keeps
  both installations; build + tests are clean.

> §2.5 ("multiple installations per hex") is now reconciled with the no-overlap
> rule: footprints only have to be disjoint **inside one site grid**.

### M4 — Multi-body (done)

**Outcome:** a second body (Mars) bakes and plays alongside the unchanged Moon
bake.

**Status:** shipped 2026-11-18 · **148 tests passing** · decisions D23–D29.
Checkpoints: pipeline `451cc6c`, catalogues `4be9469`, shell `3fc052c` +
`0c3f1b0`, two Mars fixes `300dafc` + `76b94aa`, save isolation `eb17f2f`, frame
gate `ec6bffa`, CI `15e96df`.

Implemented:

1. **Pipeline** — `pipeline/spm/bodies.py` registers Moon and Mars; `bake_mars.py`
   uses MOLA MEGDR 16 ppd, the USGS 15M geologic map and the Robbins & Hynek 2012
   crater catalogue. Mars keeps fine=6 / region=3 (41,162 regions, 7 biomes);
   `--synthetic` generates the full tile layout without downloads for CI. The
   Moon manifest SHA-256 remains
   `12893B7A74D6709B565519F98113E6D3F676A25E253DAEF9DF146B6224B2471A`.
2. **Per-body buildings** — `BuildingCatalogue` clones the 17 canonical Moon
   definitions for Mars with translated biome restrictions; building IDs stay
   stable across bodies, so no save migration is needed.
3. **Body selection and travel** — `--body=<key>`, per-body last-slot settings,
   `KnownBodies`, `SwitchBody()` and the `M` Solar System Map. Moon and Mars are
   READY; unmapped bodies are dim and refuse travel. `G` enters the colony view
   from the region view.
4. **Save safety** — no schema change: `bodyKey` is part of the bake fingerprint.
   Cross-body slots are listed `Incompatible`, refused before any file is
   written, and never overwritten. Fresh Mars slots autosave cleanly; empty H3
   hexes inherit MOLA elevation instead of reading 0 m.
5. **Gate and CI** — `--frame-budget=<ms>` / `--frames=<n>` measure p95 frame time
   against a measured 20.00 ms limit. `.github/workflows/ci.yml` builds/tests,
   bakes synthetic Moon + Mars fixtures, and runs five headless Godot smokes with
   the pinned 4.7.2 mono archive. No `data/` is committed.

**Acceptance criteria** — verified on 2026-11-18:

- [x] `data/moon` and `data/mars` both load; each campaign stores `bodyKey`.
- [x] A Moon save is `Incompatible` under the Mars fingerprint and stays byte-for-byte
  untouched after a refused load; no `regions/` directory is created.
- [x] `--body=mars` creates a fresh slot, and map travel keeps per-body last slots
  separate; the old Mars test save is intentionally incompatible (D27).
- [x] Moon globe p95 was `16.82 / 16.67 / 16.67 ms`; the 20 ms gate passes and a
  1 ms limit fails with exit 1. Build clean, 0 warnings; 148/148 tests.
- [x] CI workflow YAML, embedded shell blocks and the exact five-run smoke script
  were verified locally with synthetic fixtures.

### M4.5 — True-color surface overlay (done)

**Outcome:** globe, region and colony views can show real public-domain surface
imagery instead of schematic biome colours. Biome colours stay available as an
analytical overlay.

**Status:** shipped 2026-11-18 · **148 C# tests + 5 pipeline tests passing** ·
decisions D30–D32. This is a presentation-only cross-cutting milestone after M4 C7.

Implemented:

1. **P0 sources** — NASA SVS CGI Moon Kit 2019 LROC WAC colour
   (`lroc_color_poles_2k.tif`, SHA-256 `13B79742…2C4A`) and USGS Viking MDIM 2.1
   colorized Mars mosaic (`mars_viking_mdim21_clrmosaic_1km.jpg`, SHA-256
   `FDFCD335…ECB2`). Both are north-up −180..180 mosaics; the pipeline applies
   `np.roll(-width // 2)` and no vertical flip.
2. **P1 sidecar bake** — `pipeline/spm/imagery.py` and `pipeline/bake_imagery.py`
   validate the existing manifest/tile index, sample at 16 ppd, average RGB per
   fine hex and write `color.bin` + `imagery.json` without touching `manifest.json`
   or `fine.bin`. `--synthetic` generates deterministic CI fixtures.
3. **P2/P3 runtime** — `Game/Scripts/SurfaceImagery.cs` validates provenance,
   resolutions, hex count, byte length and SHA-256, then precomputes per-region
   means. `RegionView` and `ManagementView` gain per-hex imagery fills and overlay
   mode `4`; `MoonGlobe` swaps one `MultiMesh` instance-colour buffer, never
   rebuilding geometry. `Tab` toggles globe imagery/biome; `1`–`5` and `Tab` work
   in the 2D views; `--overlay=color` (aliases `colour`/`imagery`/`surface`) works
   in all three views.
4. **P4 coverage** — CI bakes synthetic imagery sidecars, runs the 5 Python
   imagery tests, and runs 11 headless Godot frame-gate smokes covering colour and
   biome fallback on all three views for both bodies. `--shot` remains outside CI.

**Acceptance criteria** — verified on 2026-11-18:

- [x] Real Moon and Mars sidecars load; globe, region and colony views show body
  colour, with per-region means used on the globe.
- [x] `--overlay=biome` restores the analytical view; without a sidecar the
  previous per-view defaults remain (biome for globe/region, terrain for colony).
- [x] Missing, short and corrupted sidecars warn and fall back without blocking
  load; save schema, `bodyKey`, `BakeFingerprint` and the manifest are unchanged.
- [x] Eight local Godot smokes plus the expanded CI matrix pass at `--frame-budget=20`;
  Moon manifest SHA-256 and Mars `generatedUtc` remain unchanged.
- [x] Build clean with 0 warnings; 148/148 C# tests and 5/5 Python imagery tests.

### M5 — Content

**Outcome:** a first playable mission loop.

Scope: tech tree UI, contracts/missions, events, science points, rover logistics
between regions, rover fleet state, failure/repair.

### M6 — Hardware designer

Unchanged from plan §6.2: engine designer, parameter → outcome model, families,
research speed, reliability rolls, factories, delegated manager.

## Cross-cutting backlog

Not tied to a milestone; pull in when they start hurting.

| Item | Why |
|---|---|
| CI (build + tests + headless smoke) | **Done in M4** — `.github/workflows/ci.yml` with synthetic bakes and frame-gate smoke |
| Screenshot regression harness | `--shot` already works; wire it to reference images |
| Save-corruption fuzz tests | The store already quarantines; prove it under garbage |
| Globe performance budget | **Done in M4** — 20 ms p95 gate; keep it before adding more bodies |
| Real-life surface overlay | **Done in M4.5 (D30–D32)** — optional `color.bin` sidecar, real Moon/Mars imagery, biome fallback, no save/fingerprint change |
| Replay/determinism harness | M1 covers same-seed ticks; extend it to longer event sequences |
| `.editorconfig` + analyzers | Current tree is consistent by convention only |
| Localization, audio, LICENSE, changelog | Release hygiene |

## Open questions

| # | Question | Status |
|---|---|---|
| 1 | Rover default speed: code uses **10 km/h**, plan §2.3 says testing default **8 km/h** | **Drift — decide in M5** (speed is a rover property) |
| 2 | Fine hexes per region: plan says ~2,400 (res 7), the bake uses **343** (res 6) | **Drift — plan text is stale; res 6 is the real choice** |
| 3 | Multiple installations per hex (plan §2.5) vs no-overlap footprints | **Decided in M3:** one site per fine hex; overlap is checked only among local cells |
| 4 | Are `RequiredTech` and `Cost` authoritative or advisory? | **Decided in M1:** authoritative; both enforced in `CampaignState.Preview`/`TryPlace` |
| 5 | Slot metadata file vs additive fields in `campaign.json` | **Decided in M2:** sibling `slot.json` cache (D15); v1 stays unchanged |
| 6 | Does the site view replace or complement the colony view? | **Decided in M3:** site view replaces fine-hex placement; colony view stays a region-scale inspector |
| 7 | Time scale: real hours vs accelerated game hours | **Decided in M1:** 1 real s = 1 game h at 1×; 1×/2×/4×, 0.25 h fixed steps |
| 8 | Save migration strategy when the bake fingerprint changes | Open; likely remap by H3 index |
| 9 | Landmark crater special cases (plan §2.3) | Deferred |
| 10 | Site-view build-grid dimensions | **Decided in M3:** radius-3 axial disk, 37 local cells |
| 11 | 3D building layer: migrate the colony view to 3D vs. hybrid 2D terrain + 3D buildings (plan §2.7) | **Open** — deferred until 3D assets are integrated; M3 kept procedural prisms |
| 12 | Art source/licence policy for committed models (plan §2.7) | CC0 preferred; decide before the first asset |
