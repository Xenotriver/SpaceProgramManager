# Decisions

A short ADR-style log. Each entry records **what was decided**, **why**, and what
it costs. Commit hashes refer to `main`. If you change one of these, update the
entry rather than deleting it.

---

## D1 — `Sim` is engine-free

**Commit:** `9c503a9` · **Status:** active

**Decision.** All game rules and state live in the `Sim` class library. `Sim`
never references Godot. Rendering, input and file-path resolution live in
`Game/Scripts/`.

**Why.** Godot types cannot be instantiated in a plain xUnit run, so anything in
`Sim` is testable without an editor, a viewport, or a GPU. It also keeps the rules
portable if the engine ever changes.

**Cost.** Data has to cross the boundary as plain values (H3 indices, `double`,
records) instead of engine resources. Views must re-derive geometry they could
otherwise have stored.

---

## D2 — Bake offline, stream at runtime

**Commits:** `7af85f3`, `2bb1200` · **Status:** active

**Decision.** The Python pipeline resolves every hex's biome, elevation and crater
class and writes `manifest.json` + `fine.bin`. The runtime only reads tiles, one
region at a time, via `RandomAccess`.

**Why.** Classification is raster-heavy (DEM, geology, 238 MB crater CSV) and
minutes-slow. The game must not carry GDAL, NumPy or that cost. It also makes the
data reproducible and inspectable without running the game.

**Cost.** Any rule change that affects baked data requires a re-bake, which
invalidates saves through the fingerprint gate (D6).

---

## D3 — Two H3 levels: region res 3, fine hex res 6

**Commit:** `7af85f3` · **Status:** active, **documented drift**

**Decision.** Regions are H3 resolution 3 (~60 km); buildable cells are resolution
6 (~0.9 km edge). Each region has **343** fine hexes (286 on the 12 pentagons).

**Why.** Resolution 6 gives a ~1 km edge on the Moon, matching the design target,
while keeping per-region tiles small enough (1.4 KB) to load instantly.

**Drift.** Plan §2.1 records resolution 7 / ~2,400 hexes as the original intent, now
annotated with a drift note; §4.4 and §11 use the shipped figures. The bake and the
runtime use res 6 / 343. The plan text is stale; the code is the decision. Recorded
in [`roadmap.md`](roadmap.md#open-questions).

**Cost.** A region is coarser than originally envisioned, so a region view holds
fewer, larger hexes. If res 7 is ever wanted, it is a `--fine-res` change plus a
re-bake plus a save migration.

---

## D4 — Craters are a rim-band overlay, not a biome

**Commit:** `7af85f3` · **Status:** active

**Decision.** Craters are stored as a per-hex class (`none` / `single` / `multi`)
and only affect movement. Multi-cell craters penalise the **rim band** (default
one ring), not the whole floor.

**Why.** Craters ≥ 20 km cover ~75% of the Moon, so "inside a crater" is not a
useful biome. Penalising whole floors would make most of the surface slow; rim
bands keep basins drivable and model the real obstacle.

**Cost.** Crater interiors are as fast as their biome, which is a deliberate
simplification. Landmark-crater special cases remain open.

---

## D5 — Campaign state is owned by `CampaignState`, not by views

**Commit:** `afccc24` · **Status:** active

**Decision.** `CampaignState` owns buildings, occupancy and campaign metadata.
`RegionView` and `ManagementView` read it and call `Preview` / `TryPlace` /
`TryRemove` / `Clear`. They never keep authoritative building state.

**Why.** The same region is shown by two different views plus a save file. Any
duplicated ownership would drift immediately. One owner also makes the placement
rules unit-testable without a viewport.

**Cost.** Views rebuild cached geometry after every mutation event instead of
patching it in place.

---

## D6 — JSON shards per region, guarded by a bake fingerprint

**Commit:** `d5df2dc` · **Status:** active

**Decision.** A save slot is `campaign.json` (meta + fingerprint) plus one
`regions/<h3>.json` per region with buildings. Writes are atomic
(`.tmp` → `File.Move`, previous file kept as `.bak`). Unreadable shards are moved
to `.corrupt`. A save is only accepted when `bodyKey`, both resolutions, region
count and `generatedUtc` match the loaded bake.

**Why.** One file per region keeps writes small and makes a single bad region
recoverable without losing the campaign. The fingerprint prevents silently
interpreting old buildings against a different world.

**Cost.** More files to manage, and any re-bake invalidates saves by design until
a migration exists. See [`save-format.md`](save-format.md).

---

## D7 — Footprints are derived, never trusted

**Commit:** `d5df2dc` · **Status:** active

**Decision.** Only the centre hex of a building is stored. On load,
`CampaignState.InstallRegion` rebuilds each footprint from its definition, drops
records that are unknown, out of region, overlapping, or boundary-clipped, and
warns about each drop.

**Why.** Footprint rules are code, not data. Storing derived geometry invites
saves that contradict the current rules.

**Cost.** A definition change can silently invalidate saved buildings. The
warnings are the only signal, which is why M2 adds on-screen notifications.

---

## D8 — One region tile in memory; globe is a single `MultiMesh`

**Commits:** `69b178a`, `2bb1200` · **Status:** active

**Decision.** The globe draws all 41,162 region polygons as one `MultiMesh`. The
runtime holds exactly one `RegionTile` at a time.

**Why.** The full fine-hex set is 14.1 M cells. Streaming per region keeps memory
flat and load times imperceptible, while a single `MultiMesh` keeps the globe at
one draw call.

**Cost.** Cross-region operations (routes, global search, economy) must load
regions explicitly. A future globe needs a budget because instance transforms are
updated on rotate/pan.

---

## D9 — Region view uses a gnomonic tangent plane

**Commit:** `2eafc04` · **Status:** active

**Decision.** A region's hexes are projected onto the tangent plane at the region
centre with `plane = (v·east, v·north) / (v·centre)` and drawn in 2D. Picking is
nearest projected cell centre, not polygon containment.

**Why.** A region spans ~60 km, so a tangent-plane approximation is visually exact
and far cheaper than spherical rendering. It also gives the isometric view a
plane it can reuse.

**Cost.** The projection is per-region and must be recomputed on load; it is not
valid for global views.

---

## D10 — Colony view: single painter-sorted isometric mesh

**Commit:** `947fa9b` · **Status:** active

**Decision.** The region-scale isometric view builds one `ArrayMesh` of prisms,
sorted back-to-front by `x + y` depth, and draws it with one call. Overlays swap
vertex colours and rebuild the mesh. Elevation is exaggerated by a scale factor.

**Why.** 343 prisms as individual nodes would be slow and awkward to sort. One
mesh keeps the frame cost flat and makes relief exaggeration a cheap rebuild.

**Cost.** Any change to geometry (overlay, orbit, relief) rebuilds arrays. Fine
for 343 cells; revisit before larger regions.

---

## D11 — Orbit the colony camera by rotating the plane in 60° steps

**Commit:** `b3b09a1` · **Status:** active

**Decision.** `Q`/`E` (and `--rot`) rotate the tangent plane about a pivot before
projection. Angles snap to 60° because a 60° rotation maps the hex lattice onto
itself.

**Why.** Rotating the mesh in 3D would break the 2D painter sort and change prism
shapes. Rotating the plane keeps every prism identical and only changes the
silhouette, so picking and depth stay correct.

**Cost.** Only six distinct orientations exist. Free-angle orbit would need a real
3D camera and a different sort.

---

## D12 — "Colony view" and "site view" are different things

**Commit:** documentation, 2026-11-15 (on top of `b3b09a1`) · **Status:** active

**Decision.** **Colony view** = the region-scale isometric management view that
exists today (`ManagementView`). **Site view** = the per-hex local build grid from
plan §2.6, scheduled as M3.

**Why.** The original plan called the local per-hex view a "site view"; the
implemented isometric view was variously called "management" and "colony". Two
names for one thing and one name for two things caused real confusion.

**Cost.** Plan §2.6 and §6.1 must be read with this mapping. The M3 milestone is
where "multiple installations per hex" (plan §2.5) is actually implemented.

---

## D13 — Child order is verified against h3-py

**Commit:** `72e453b` · **Status:** active

**Decision.** `ChildOrderTests` pins the exact child enumeration order for a
343-cell and a 286-cell region against values produced by Python `h3`.

**Why.** The pipeline writes records in Python's child order; the runtime indexes
them by C# `pocketken.H3` order. If the two ever diverge, every hex silently gets
another hex's biome.

**Cost.** The test has hardcoded indices, so an H3 major-version bump must update
both sides deliberately — which is the point.

---

## D14 — Longitude frames are reconciled in the pipeline

**Commit:** `fd9b926` · **Status:** active

**Decision.** `read_geology` rolls the USGS raster from its `-180..180` frame into
the LOLA DEM's `0..360` frame before resampling. `cli.py` passes
`UGM_LON_WEST=-180`, `LOLA_LON_WEST=0`.

**Why.** Index-based resizing placed all maria 180° off. Biome histograms still
looked plausible because it was a column permutation, so only a rendered preview
caught it.

**Cost.** Every new raster source must declare its longitude convention. The
preview tool (`preview_moon.py`) stays in the repo as the cheap check.

---

## D15 — Slot summary is a sibling `slot.json`, never schema

**Commit:** M2, 2026-11-16 · **Status:** active

**Decision.** Each slot keeps a `slot.json` next to `campaign.json`. It holds the
display name, timestamps, cached counts and the last view snapshot. It is a
rebuildable cache plus UI state; `campaign.json` and the region shards stay the
only authoritative data. The summary is trusted only while its `updatedUtc`
matches the campaign's.

**Why.** The slot list needs names, counts and a camera snapshot without parsing
every shard. Putting that in `campaign.json` would change the v1 schema for
non-game data. A sibling file can be deleted, ignored by older builds, and rebuilt
by scanning `regions/*.json`.

**Cost.** Two files can briefly disagree; the timestamp rule resolves that by
rebuilding. A slot with a corrupt `campaign.json` cannot be repaired from its
summary, by design.

---

## D16 — Slot switching reloads the scene

**Commit:** M2, 2026-11-16 · **Status:** active

**Decision.** Continue, Play slot, New campaign and Main menu set a static pending
slot (or menu flag) and call `SceneTree.ReloadCurrentScene()`. The outgoing scene
flushes its campaign and view snapshot in `_ExitTree`; the new scene reads the
pending state.

**Why.** Views cache per-region geometry, the campaign is installed per region,
and `CampaignService` owns one slot. Rebuilding all of that in place invites
half-swapped state. A scene reload is the engine's normal, cheap way to guarantee
a clean slate for a 41k-region globe and one open tile.

**Cost.** A visible reload (one frame) when switching slots, and static flags are
needed to carry the target across the reload. This is acceptable while the world
load is already part of startup.

---

## Open decisions

Tracked in [`roadmap.md`](roadmap.md#open-questions): rover speed default,
multi-installation hexes, tech/cost enforcement, time scale, and bake-change save
migration. The slot metadata layout is settled by D15.
