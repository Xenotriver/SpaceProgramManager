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

**Commit:** `afccc24`, updated M3 2026-11-17 · **Status:** active

**Decision.** `CampaignState` owns installations, site occupancy and campaign
metadata. `SiteView` is the only view that calls `Preview` / `TryPlace` /
`TryRemove`; it clears one site by repeated `TryRemove`. `RegionView` and
`ManagementView` are read-only inspectors. No view keeps authoritative
installation state.

**Why.** The same region is shown by three different views plus a save file. Any
duplicated ownership would drift immediately. One owner also makes the placement
rules unit-testable without a viewport.

**Cost.** Views rebuild cached geometry or marker lists after every mutation
event instead of patching them in place.

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

**Decision.** Only a building's parent hex and `localCenter` are stored. On load,
`CampaignState.InstallRegion` rebuilds each local footprint from its definition,
drops records that are unknown, outside the region or local grid, or overlapping
inside the same site, and warns about each drop.

**Why.** Footprint rules are code, not data. Storing derived geometry invites
saves that contradict the current rules. Local occupancy also lets one fine hex
hold several installations without H3-scale overlap.

**Cost.** A definition change can silently invalidate saved installations. The
warnings are the only signal, so load reports stay visible in the automation log.

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

**Commit:** documentation, 2026-11-15 (on top of `b3b09a1`), updated M3 2026-11-17 · **Status:** active

**Decision.** **Colony view** = the region-scale isometric management view
(`ManagementView`). **Site view** = the per-hex local build grid from plan §2.6,
shipped in M3 (`SiteView`).

As of M3 the region and colony views are inspectors with aggregate site markers;
only the site view places or removes installations.

**Why.** The original plan called the local per-hex view a "site view"; the
implemented isometric view was variously called "management" and "colony". Two
names for one thing and one name for two things caused real confusion.

**Cost.** Plan §2.6 and §6.1 must be read with this mapping. M3 implemented
"multiple installations per hex" (plan §2.5) in the site view and retired
fine-hex placement from the other two views.

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

## D17 — Fixed-step clock at 1 real second = 1 game hour

**Commit:** M1, 2026-11-17 · **Status:** active

**Decision.** `SimulationClock` accumulates real seconds and emits 0.25 game-hour
steps. A session starts at 1× (one real second per game hour); the pause menu also
offers 2× and 4×, and `--time=0` pins the clock for automation. `CampaignState.Tick`
accepts any duration but subdivides it into the same step.

**Why.** Fixed steps make construction, production and idling order reproducible
and keep a long frame from changing outcomes. One hour per second makes a 24-hour
solar array finish in 24 seconds at 1× — visible but not tedious; 4× is available
for longer builds.

**Cost.** Time is tied to frame deltas rather than wall-clock timestamps, so a
suspended tab does not fast-forward. Only regions installed in memory are ticked;
a fleet pass over all shards is future work.

---

## D18 — `RequiredTech` and `Cost` are authoritative

**Commit:** M1, 2026-11-17 · **Status:** active

**Decision.** `CampaignState.Preview` rejects a definition whose `RequiredTech` is
locked, and `TryPlace` charges `Cost` (credits plus stockpile) atomically before it
adds a building. A failed charge changes nothing. `BuildingRegistry.GetAvailableAtSite`
still filters for UI, but it is no longer the only guard.

**Why.** A view can be wrong or bypassed by automation; the campaign owner must be
the single authority. All-or-nothing payment keeps an unaffordable placement from
leaving a half-charged treasury.

**Cost.** Tech selection has no UI yet (M5), so `UnlockTech` is currently reachable
only from code and tests. Definition authors must keep costs affordable in the
starting stockpile or mark the building as later-game content.

---

## D19 — Save v2 migrates v1 without overwriting it

**Commit:** M1, 2026-11-17 · **Status:** active

**Decision.** `campaign.json` and region shards moved to version 2. Loading a v1 save
is read-only: the runtime derives an RNG seed from the stored `createdUtc`, fills
missing starting credits/stockpile, and defaults new building flags. The file is
upgraded only on the next save. M3 later moved the schema to v3 while keeping this
v1 migration chain; versions outside the supported range are refused and left
untouched.

**Why.** The M2 shell shipped with v1 saves in the wild, so a silent rewrite on load
would risk losing player data. Deriving the seed from the save rather than the load
time also keeps repeated loads deterministic.

**Cost.** `LoadMeta` carries a migration branch that must be kept until v1 support
is dropped. Every new schema field needs a migration rule and a fixture test.

---

## D20 — Site view replaces fine-hex placement

**Commit:** M3, 2026-11-17 · **Status:** active

**Decision.** `SiteView` is the only renderer that places or removes installations.
A click on a fine hex in the region or colony view selects it; `Enter` opens that
hex as a site. `Esc` returns to the previous view. `RegionView` and
`ManagementView` show one aggregate marker per occupied fine hex (count + most
urgent status) and route all mutations through `CampaignState` via `SiteView`.

**Why.** A fine hex is a container, not a one-building slot. Keeping placement in
the two region-scale views would duplicate occupancy rules and could not express
local cells. The inspector/marker split also keeps route planning and terrain
inspection unchanged.

**Cost.** One extra mode and navigation rule to learn. CLI `--place` now requires
`--view=site` and takes local `q,r` cells instead of hex ordinals.

---

## D21 — Radius-3 axial site grid

**Commit:** M3, 2026-11-17 · **Status:** active

**Decision.** Each site is the axial disk `max(|q|, |r|, |q+r|) <= 3`: 37 cells,
centre first. Radius 0 occupies one cell; radius 1 occupies the centre plus its
six neighbours. A radius-1 footprint centred in ring 3 fails disk validation
naturally; no special-case clipping rule was added.

**Why.** The disk is large enough for several installations per fine hex, small
enough to render as one isometric diorama, and deterministic without a stored
tile-size table. Reusing `IsoProjection` avoids a second projection engine.

**Cost.** Local `(q,r)` coordinates are separate from H3 indices, so saves,
ghosts, CLI flags and docs all have to carry both coordinate systems.

---

## D22 — Full v1/v2 → v3 shard migration

**Commit:** M3, 2026-11-17 · **Status:** active

**Decision.** Region shards now store `sites` → `installations` with `localCenter`.
Legacy v1/v2 flat `buildings` records are converted on load into one site per
distinct `centerHex`, with local `(0,0)` and all economy/status fields preserved.
The v1 meta migration stays intact; supported versions are `1`–`3`, unknown
versions are refused, and no file is overwritten until an explicit save.
Footprints remain derived, never stored.

**Why.** Existing saves must survive the move from region-scale footprints to
site-scoped occupancy. The old no-overlap rule makes each legacy centre unique, so
conversion needs no conflict resolution, and read-only loading preserves the
no-overwrite guarantee.

**Cost.** `CampaignStore` keeps three migration paths and separate legacy/current
DTOs. Definition changes can still invalidate derived footprints, so load warnings
remain important.

---

## D23 — Mars is the second body

**Commits:** M4 C1, 2026-11-18 (`933f192`, `451cc6c`) · **Status:** active

**Decision.** Mars, not Earth, is the second playable body. Its bake keeps craters
and has no oceans; the Moon pipeline is generalized around a `source_profile` so
the existing Moon fetch path is unchanged.

**Why.** Mars is the closest analogue to the Moon's data stack: global elevation,
geology and crater catalogues are public, and the existing movement/site scoring
rules need no ocean or atmosphere concepts.

**Cost.** Real Mars inputs are larger and use a different longitude convention
(MOLA's 0..360 grid), so geology is rasterized at 1° and rolled before
upsampling. Synthetic fixtures cover layout in CI, not science fidelity.

---

## D24 — Shared H3 resolutions, unchanged Moon bake

**Commit:** M4 C1, 2026-11-18 (`451cc6c`) · **Status:** active

**Decision.** Mars uses fine=6 / region=3, the same 41,162 regions and 343 fine
hexes per region as the Moon; only the ground area per cell changes. The Moon bake
is reused unchanged, with manifest SHA-256
`12893B7A74D6709B565519F98113E6D3F676A25E253DAEF9DF146B6224B2471A`.

**Why.** Keeping the resolutions identical means one tile format, one child-order
test and one set of save fields across bodies. Not re-baking the Moon preserves
its `generatedUtc` fingerprint and every existing Moon save.

**Cost.** A Mars region is physically larger than a Moon region, so gameplay
distances are body-specific even though indices match. The Mars bake was later
regenerated for the elevation fix, which deliberately invalidated its old test
save.

---

## D25 — Body-independent building IDs

**Commit:** M4 C2, 2026-11-18 (`4be9469`) · **Status:** active

**Decision.** `BuildingCatalogue.RegistryFor(bodyKey)` returns the canonical 17
building IDs for the Moon and clones of them for Mars with translated biome
restrictions (mare/lowlands, midlands/highlands and polar tiers). IDs never change
between bodies.

**Why.** Save shards store definition IDs. Translating allowed biomes at runtime
lets a Mars campaign use the same buildings and schema with no migration path.

**Cost.** The translation table must be kept aligned with both biome registries.
Volcanic and canyon biomes stay excluded from the translated allowed lists.

---

## D26 — Body choice is shell state, not campaign inheritance

**Commits:** M4 C3, 2026-11-18 (`3fc052c`, `0c3f1b0`) · **Status:** active

**Decision.** `--body=<key>` defaults to Moon; `GameSettings` stores the last slot
per body, with the legacy value migrated into the Moon entry. `SwitchBody()` saves
the current campaign, validates the target bake, and reloads through a fresh or
existing target slot. The `M` Solar System Map offers schematic travel; `G` keeps
its colony-view meaning from the region view.

**Why.** Continue must never jump from a Moon campaign into a Mars slot. A map
gives a discoverable body switch without adding a second menu hierarchy.

**Cost.** Body switching reloads the scene. The map is deliberately schematic:
fixed orbits and angles, not astronomical positions.

---

## D27 — Cross-body saves are refused, not migrated

**Commits:** M4 post-C3 and C4, 2026-11-18 (`76b94aa`, `eb17f2f`) · **Status:** active

**Decision.** `bodyKey` remains in `campaign.json` and is compared with the rest of
`BakeFingerprint`. `CampaignSlotStore.List` marks cross-body slots `Incompatible`;
`CampaignService.Load()` refuses them before writing anything. The old Mars test
save created before the elevation re-bake is intentionally incompatible, with no
migration or archive.

**Why.** H3 indices alone do not make two bodies interchangeable, and guessing a
remap could corrupt a campaign. Refusing is observable and keeps every original
file intact.

**Cost.** Players must start a fresh slot when a body's bake fingerprint changes.
A future compatible re-bake migration is still an open question.

---

## D28 — Frame gate at a measured 20 ms

**Commit:** M4 C5, 2026-11-18 (`ec6bffa`) · **Status:** active

**Decision.** `--frame-budget=<ms>` forces redraws, skips 12 warmup frames and
compares p95 over 180 measured frames by default; `--frames` allows 30–3600. The
limit is 20.00 ms, derived from Moon globe p95 runs of `16.82 / 16.67 / 16.67 ms`
rather than chosen arbitrarily. PASS exits 0, FAIL exits 1.

**Why.** The globe holds 41,162 region instances, and a regression must be
catchable in automation before more bodies or imagery are added.

**Cost.** CI shortens the run to 30 frames, so it is a smoke rather than a
precision benchmark. Results still vary with runner GPU/CPU, so the gate measures
the chosen view under forced redraw rather than interactive idle time.

---

## D29 — CI uses synthetic fixtures and a pinned mono Godot

**Commit:** M4 C6, 2026-11-18 (`15e96df`) · **Status:** active

**Decision.** `.github/workflows/ci.yml` installs .NET 8 and Python 3.13, bakes
synthetic Moon and Mars fixtures into `SPM_DATA`, sets `SPM_REQUIRE_DATA=1`,
builds/tests, then downloads the official Godot 4.7.2 mono Linux zip pinned to
SHA-256 `129f82db7bafd54ae14bb5bb284041c73860e8c7a009a3a026ca5e946cbff247`.
Five headless view runs initially used `--frame-budget=20 --frames=30`; M4.5
extends the same job to eleven runs covering imagery and biome fallback. `--shot`
is excluded.

**Why.** `godotengine/setup-godot` does not exist in GitHub's API, and a pinned
archive is reproducible. Generated fixtures keep `data/` out of the repository
while `SPM_REQUIRE_DATA=1` stops real-bake tests from silently skipping.

**Cost.** Each run downloads a ~100 MB archive and uses synthetic terrain, so CI
does not validate real science sources or screenshots. Headless `--shot` hangs in
this automation path, so the frame-gate run doubles as the smoke.

---

## D30 — Surface colour is an optional presentation sidecar

**Commit:** M4.5, 2026-11-18 · **Status:** active

**Decision.** Imagery lives in `data/<body>/color.bin` (three RGB bytes per fine
hex in `fine.bin` child order) plus `imagery.json` for provenance and validation.
`SurfaceImagery` in `Game/Scripts/` checks body key, resolutions, hex count, byte
length and SHA-256. Any missing, short, wrong-body, wrong-resolution or corrupt
input logs a warning and falls back to biome colours without blocking load.
`manifest.json`, the tile format, save schema, `bodyKey` and `BakeFingerprint` are
unchanged; `Sim` never sees imagery.

**Why.** The existing bake and saves must stay valid for both bodies, and a
presentation experiment must never be able to corrupt authoritative data. A
sidecar also lets CI use deterministic synthetic colour without touching science
sources.

**Cost.** Each body adds a 42 MB optional file read at startup and a SHA-256 pass.
Players without the sidecar keep the previous schematic look.

---

## D31 — Pinned public-domain imagery with one alignment rule

**Commit:** M4.5, 2026-11-18 · **Status:** active

**Decision.** The Moon uses NASA SVS CGI Moon Kit `lroc_color_poles_2k.tif`
(SHA-256 `13B79742…2C4A`); Mars uses USGS Viking MDIM 2.1 colorized
`mars_viking_mdim21_clrmosaic_1km.jpg` (SHA-256 `FDFCD335…ECB2`). Both are
north-up −180..180 mosaics, so the sampler applies only
`np.roll(rgb, -width // 2, axis=1)` with no vertical flip. Sources are sampled at
16 ppd, averaged per fine hex, and their exact URLs, licences and credits are
copied into `imagery.json`.

**Why.** Explicit products, checksums and alignment prevent a visually plausible
but mirrored or offset overlay. Public-domain/non-copyright US government imagery
keeps the repository free of licensed assets (`data/` remains gitignored anyway).

**Cost.** The Mars source is 228 MP and needs a verified checksum before Pillow's
decompression-bomb limit is relaxed for that one decode. The images are aesthetic
global mosaics, not scientific multispectral products.

---

## D32 — True colour is the default; biome stays one key away

**Commit:** M4.5, 2026-11-18 · **Status:** active

**Decision.** When a valid sidecar is present, globe, region and colony views
default to real surface colour for every slot. `Tab` toggles globe imagery/biome;
`Tab` and `1`–`5` cycle region/colony overlays. `--overlay=color` (with
`colour`/`imagery`/`surface` aliases) works in all three views and
`--overlay=biome` restores the analytical palette. An explicit CLI overlay wins;
saved `slot.json` overlay text is used only when imagery is unavailable. The
globe uses one precomputed mean colour per region and re-uploads only instance
colours on toggle.

**Why.** The locked M4.5 intent is that bodies look like themselves immediately,
while analytical biomes remain reachable for scoring and comparison. Reusing the
existing UI string keeps the save schema at v3 with no migration.

**Cost.** A player who prefers biome colours must select them each session unless
they pass `--overlay=biome`; without imagery, the previous per-view defaults
remain (biome for globe/region, terrain for colony).

---

## D33 — Cheap chamfer-and-slope test before any terrain redesign

**Commit:** `2378137` · **Status:** superseded by D34

**Decision.** The colony view keeps H3 fine hexes as the only authoritative
terrain cells. Each prism now has a small chamfered rim and a directional
vertex-colour multiplier computed from neighbouring hex elevations. The gradient
is presentation-only: picking, placement, movement costs, site scoring and the
rover route still read the original per-hex elevation from `RegionTile`. No
raster heightmap, corner-averaged faceted mesh or multi-region terrain view was
added.

**Why.** Flat prisms read as stairs when imagining rover movement. A test that
changes only mesh shading and a small rim is cheap, keeps the hex grid legible as
building slots, and answers whether the blockiness is actually a problem before
a larger geometry rewrite. It also avoids inventing interpolated elevation data
that the bake does not contain.

**Cost.** Top shading varied with relief exaggeration and camera orbit, and
border hexes used fewer neighbours. On visual review the result was not pleasant
enough, so this test was superseded within the same day by D34. It remains useful
as evidence that palette-only tweaks were insufficient.

---

## D34 — Faceted hex surface replaces chamfered prisms

**Commit:** 2026-11-19 · **Status:** active

**Decision.** `ManagementView` renders each fine hex as a six-triangle fan: the
centre vertex uses the cell's authoritative elevation, and each corner vertex uses
the mean elevation of every cell in the loaded region that shares that H3 vertex.
Shared corners are keyed from the gnomonic tangent-plane point and reused exactly,
so neighbouring cells meet without a vertical wall. Only edges incident to one
cell receive a skirt. Facets are flat-shaded from their plane normal while every
overlay keeps its existing flat per-hex colour.

**Why.** The D33 chamfer test proved that shading alone did not make the terrain
pleasant to look at. A corner-averaged surface removes the stair silhouette while
keeping one visual cell per buildable H3 hex, so picking, site markers, placement
and overlays still line up exactly. It avoids inventing a raster heightmap and
avoids treating interpolated corner heights as authoritative data.

**Cost.** Corner heights are smoothed at region borders because neighbouring
tiles are not loaded, and sharp local elevation extremes are softened. A future
AreaView would improve those seams. Nothing changes in `Sim`, the tile format,
save schema, `bodyKey` or `BakeFingerprint`; movement and scoring still read the
original per-hex elevation.

---

## Open decisions

Tracked in [`roadmap.md`](roadmap.md#open-questions): rover speed default,
bake-change save migration, and 3D building assets/presentation. Multi-installation
hexes, the site/colony split and the local grid size are settled by D20–D22;
tech/cost enforcement and time scale are settled by D18 and D17; the slot metadata
layout is settled by D15. M4 body, catalogue, save-isolation, frame-gate and CI
choices are settled by D23–D29, M4.5 imagery by D30–D32, and the terrain
presentation by D33–D34.
