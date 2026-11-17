# Roadmap

> **Mirrored in `docs/roadmap.md` and `~/.opencode/plan/game_development_plan.md` — update both.**
>
> This file is the status-and-milestones view. The plan document keeps the full
> design spec (biomes, movement maths, construction rules, hardware outline).

## Status snapshot

Anchor: **2026-11-17**, M1 simulation loop, **113 tests passing**.

| Area | State | Evidence |
|---|---|---|
| Moon data pipeline | **Done** | `data/moon`: 41,162 regions, 14,117,882 fine hexes, 343 hexes/region, 10 biomes, `fine.bin` 54.6 MB |
| Sim core | **Done** | H3, biomes, movement, routing, world loader, site scoring, buildings, campaign, save store, slot catalogue |
| Godot runtime | **Done** | Globe, region view, isometric colony view, placement/removal, save/load, CLI automation |
| Campaign shell | **Done (M2)** | Main menu, multi-slot saves, Continue, manual save, toasts, pause scaffold, search, settings |
| Simulation loop | **Done (M1)** | `Sim/Time` clock + seeded RNG, construction lifecycle, economy, power/life support, tech enforcement, save v2, live controls |
| Site view (per-hex grid) | **Planned (M3)** | Only the region-scale colony view exists |
| Multi-body | **Planned (M4)** | `Main.OpenMoon()` is hardcoded |
| Hardware designer | **Planned (M6)** | Outline only (plan §6.2) |

### What "done" means today

The player can: start or continue a named campaign from the main menu, keep
several isolated save slots, save manually, rotate the Moon globe, search an H3
index, open a region, inspect hexes across four overlays, plan a rover route,
place/remove buildings from either the 2D region view or the isometric colony
view, and have all of it persist across launches against a fingerprinted bake.

The player can also pass time with pause/1×/2×/4×, watch buildings finish
construction, earn/spend credits and stockpile resources, see power or life-support
shortfalls called out in the HUD, and find occupied hexes in every overlay.

The player cannot: pick a tech in the UI, rename a campaign, or play a second
body yet.

### Naming (locked)

- **Colony view** — the region-scale isometric management view that exists today
  (`ManagementView`, `--view=management`, `M` from the region view). It shows all
  343 hexes of one region as prisms.
- **Site view** — the per-hex local build grid from plan §2.6. It does not exist
  yet and is scheduled as **M3**. It is a different scale, not a rename.

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

**Status:** shipped 2026-11-17 · **113 tests passing** · resolves open questions 4
and 7.

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
   Unknown/incompatible saves are still refused, never overwritten.
8. **Live time controls** — pause/1×/2×/4× in the pause overlay, `Space` to toggle
   pause, HUD day/hour and economy line, 5-second autosave, `--time` and
   `--advance` automation hooks.
9. **Pentagon neighbour test** — 5 distinct neighbours on a pentagon cell.
10. **Occupied-region markers** — green rings on the globe and status-coloured
    pins/outlines in biome, terrain, score and buildings overlays of both the
    region and colony views.

**Acceptance criteria** — verified on 2026-11-17:

- [x] A habitat plus a solar array reach `Operational` after their build times.
- [x] A region with negative power marks consumers `Idle` and the HUD says why.
- [x] Credits decrease by exactly the building cost; an unaffordable placement is refused.
- [x] Save → reload preserves time, progress, status and treasury.
- [x] Deterministic tick tests: same seed + same tick count = same state.
- [x] A v1 fixture loads and migrates under v2.
- [x] Occupied regions stay findable in every globe/region/colony overlay.

### M3 — Site view (plan §2.6)

**Outcome:** selecting a fine hex opens a local build grid inside that hex, and a
hex can hold several installations.

Scope: per-hex grid; isometric presentation reusing `IsoProjection` and the colony
renderer; entry/exit from the region view; multiple installations per hex; local
placement rules; persistence in the region shard.

> **Art direction (noted 2026-11-16, not locked).** Buildings are **3D models**
> in a **fixed 2.5D isometric view**; the camera does not freely orbit. This does
> not reopen "no free-camera 3D" (plan §2.6). It is a separate decision from the
> site view and is tracked as open questions 11–12; procedural prisms remain the
> placeholder until an engine 3D layer exists.

**Acceptance criteria:** two installations on one hex's grid, saved and restored;
the colony view still shows per-hex occupancy summaries.

> This milestone is where §2.5 ("multiple installations per hex") is
> reconciled with today's rule that building footprints may not overlap.

### M4 — Multi-body

**Outcome:** a second body (Earth or Mars) bakes and plays.

Scope: pipeline generalization (`bodies.py`), body selection in the shell,
per-body biome/building tables, campaign scoped per body, data-root layout for
multiple bodies. Replace the hardcoded `Main.OpenMoon()`.

**Acceptance criteria:** two bodies in `data/`; a campaign stores its `bodyKey`;
switching bodies does not mix saves.

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
| CI (build + tests + headless smoke shot) | 113 tests only run when someone remembers |
| Screenshot regression harness | `--shot` already works; wire it to reference images |
| Save-corruption fuzz tests | The store already quarantines; prove it under garbage |
| Globe performance budget | 41,162 instances today; add a frame-time gate before Earth |
| Replay/determinism harness | M1 covers same-seed ticks; extend it to longer event sequences |
| `.editorconfig` + analyzers | Current tree is consistent by convention only |
| Localization, audio, LICENSE, changelog | Release hygiene |

## Open questions

| # | Question | Status |
|---|---|---|
| 1 | Rover default speed: code uses **10 km/h**, plan §2.3 says testing default **8 km/h** | **Drift — decide in M5** (speed is a rover property) |
| 2 | Fine hexes per region: plan says ~2,400 (res 7), the bake uses **343** (res 6) | **Drift — plan text is stale; res 6 is the real choice** |
| 3 | Multiple installations per hex (plan §2.5) vs no-overlap footprints (today) | Resolve in **M3** |
| 4 | Are `RequiredTech` and `Cost` authoritative or advisory? | **Decided in M1:** authoritative; both enforced in `CampaignState.Preview`/`TryPlace` |
| 5 | Slot metadata file vs additive fields in `campaign.json` | **Decided in M2:** sibling `slot.json` cache (D15); v1 stays unchanged |
| 6 | Does the site view replace or complement the colony view? | Decide in **M3** |
| 7 | Time scale: real hours vs accelerated game hours | **Decided in M1:** 1 real s = 1 game h at 1×; 1×/2×/4×, 0.25 h fixed steps |
| 8 | Save migration strategy when the bake fingerprint changes | Open; likely remap by H3 index |
| 9 | Landmark crater special cases (plan §2.3) | Deferred |
| 10 | Site-view build-grid dimensions | Deferred to M3 |
| 11 | 3D building layer: migrate the colony view to 3D vs. hybrid 2D terrain + 3D buildings (plan §2.7) | Decide in **M3**, before asset integration |
| 12 | Art source/licence policy for committed models (plan §2.7) | CC0 preferred; decide before the first asset |
