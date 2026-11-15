# Roadmap

> **Mirrored in `docs/roadmap.md` and `~/.opencode/plan/game_development_plan.md` — update both.**
>
> This file is the status-and-milestones view. The plan document keeps the full
> design spec (biomes, movement maths, construction rules, hardware outline).

## Status snapshot

Anchor: **2026-11-15**, commit `b3b09a1`, `main` = `origin/main`, **88 tests passing**.

| Area | State | Evidence |
|---|---|---|
| Moon data pipeline | **Done** | `data/moon`: 41,162 regions, 14,117,882 fine hexes, 343 hexes/region, 10 biomes, `fine.bin` 54.6 MB |
| Sim core | **Done** | H3, biomes, movement, routing, world loader, site scoring, buildings, campaign, save store |
| Godot runtime | **Done** | Globe, region view, isometric colony view, placement/removal, save/load, CLI automation |
| Simulation loop | **Not started** | No `Sim/Time`; `GameTimeHours` never advances; buildings stay `Planned` |
| Campaign shell | **Next (M2)** | No menu, slots, notifications, search, settings |
| Site view (per-hex grid) | **Planned (M3)** | Only the region-scale colony view exists |
| Multi-body | **Planned (M4)** | `Main.OpenMoon()` is hardcoded |
| Hardware designer | **Planned (M6)** | Outline only (plan §6.2) |

### What "done" means today

The player can: rotate the Moon globe, open a region, inspect hexes across four
overlays, plan a rover route, place/remove buildings from either the 2D region
view or the isometric colony view, and have all of it persist across launches
against a fingerprinted bake.

The player cannot: pass time, finish construction, spend credits, research
anything, or start/load a named campaign from a menu.

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

### M2 — Campaign shell (next)

**Outcome:** the game has a front end and a save-slot model instead of booting
straight into the last region.

Scope:

1. **Main menu overlay** — New campaign · Continue · Load slot · Settings · Quit.
   Drawn with the existing immediate-mode HUD style; no scene restructure required.
2. **Save-slot service** over `CampaignStore` — enumerate/create/delete slots under
   `user://saves/`, slot summary (name, created, updated, building count, visited
   regions), last-played ordering. `campaign.json` stays **v1-compatible**; slot
   summary is a sibling file or optional additive fields, never a breaking change.
3. **Manual save + visible feedback** — `F5` saves; save/load failures become
   on-screen notifications instead of `GD.PushWarning` only.
4. **Notifications/toasts** — small log for save, load, quarantine, placement errors.
5. **Region search and markers** — jump to an H3 index, "go to active region", and
   visited/active markers drawn on the globe.
6. **Settings panel** — save root, default relief exaggeration, keybind reference.
7. **Pause overlay + time controls, scaffolded** — visible but disabled with
   `time system: M1`. There is no clock to pause yet; M1 fills these in.

**Acceptance criteria**

- New campaign → place a building → quit → **Continue** restores mode, region,
  camera and buildings.
- Two slots stay fully isolated; deleting a slot removes its directory and is
  reflected in the list.
- A corrupt/incompatible slot is shown as such and is never overwritten.
- `--save`, `--fresh`, `--nosave`, `--shot` automation still behave exactly as today.
- No change to the v1 save schema that breaks an existing `campaign.json`.

### M1 — Simulation loop

**Outcome:** time passes and buildings actually get built and run.

Scope:

1. **`Sim/Time`** — deterministic clock (game hours), fixed-step tick, seeded RNG
   service (required for reliability rolls later).
2. **`CampaignState.Tick(hours)`** — drives construction, status transitions,
   maintenance and production; pure and unit-testable.
3. **Construction lifecycle** — `Planned → UnderConstruction → Operational` using
   `BuildTimeHours`, `ConstructionStartTime`, `progress`; pause/resume/cancel.
4. **Economy** — charge `Cost` at placement (credits + stockpile), apply
   maintenance, aggregate per-region production/consumption.
5. **Power and life support** — per-region balance; deficits set buildings `Idle`
   instead of silently producing.
6. **Tech unlocks** — `RequiredTech` enforced in `CampaignState.Preview`, not only
   in `BuildingRegistry.GetAvailableAtSite`; minimal research state.
7. **Save v2 + migration** — new fields (`SimTime`, RNG seed, per-building
   construction timestamps) with a tested v1 → v2 migration.
8. **Live time controls** — pause/1×/2×/4× wired to the M2 scaffold.
9. **Pentagon neighbour test** — 5 neighbours on a pentagon cell (gap carried over from the Phase 1 checklist).

**Acceptance criteria**

- A habitat plus a solar array reach `Operational` after their build times.
- A region with negative power marks consumers `Idle` and the HUD says why.
- Credits decrease by exactly the building cost; an unaffordable placement is refused.
- Save → reload preserves time, progress, status and treasury.
- Deterministic tick tests: same seed + same tick count = same state.
- A v1 fixture loads and migrates under v2.

### M3 — Site view (plan §2.6)

**Outcome:** selecting a fine hex opens a local build grid inside that hex, and a
hex can hold several installations.

Scope: per-hex grid; isometric presentation reusing `IsoProjection` and the colony
renderer; entry/exit from the region view; multiple installations per hex; local
placement rules; persistence in the region shard.

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
| CI (build + tests + headless smoke shot) | 88 tests only run when someone remembers |
| Screenshot regression harness | `--shot` already works; wire it to reference images |
| Save-corruption fuzz tests | The store already quarantines; prove it under garbage |
| Globe performance budget | 41,162 instances today; add a frame-time gate before Earth |
| Replay/determinism harness | Needed once M1 introduces time and RNG |
| `.editorconfig` + analyzers | Current tree is consistent by convention only |
| Localization, audio, LICENSE, changelog | Release hygiene |

## Open questions

| # | Question | Status |
|---|---|---|
| 1 | Rover default speed: code uses **10 km/h**, plan §2.3 says testing default **8 km/h** | **Drift — decide in M5** (speed is a rover property) |
| 2 | Fine hexes per region: plan says ~2,400 (res 7), the bake uses **343** (res 6) | **Drift — plan text is stale; res 6 is the real choice** |
| 3 | Multiple installations per hex (plan §2.5) vs no-overlap footprints (today) | Resolve in **M3** |
| 4 | Are `RequiredTech` and `Cost` authoritative or advisory? | Resolve in **M1** (enforce both) |
| 5 | Slot metadata file vs additive fields in `campaign.json` | Decide in **M2**; v1 must keep loading |
| 6 | Does the site view replace or complement the colony view? | Decide in **M3** |
| 7 | Time scale: real hours vs accelerated game hours | Decide in **M1** |
| 8 | Save migration strategy when the bake fingerprint changes | Open; likely remap by H3 index |
| 9 | Landmark crater special cases (plan §2.3) | Deferred |
| 10 | Site-view build-grid dimensions | Deferred to M3 |
