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
extended the same job to eleven runs covering imagery and biome fallback, and M5
C1/C2 extended it to fifteen runs with the tech, area and fleet screens. `--shot`
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
AreaView would improve those seams. This rendering decision itself changed no
`Sim` code, tile format, save schema, `bodyKey` or `BakeFingerprint`; movement and
scoring still read the original per-hex elevation. M5 C0 later bumped the save
schema for unrelated content state.

---

## D35 — Save v4 for M5 global state

**Commit:** 2026-11-19 · **Status:** active

**Decision.** Campaign metadata is version 4. It adds three authoritative blocks:
`contracts` (active/completed/failed records with progress), `events` (pending
choices, one cooldown and a 50-entry history), and `rovers` (a campaign-wide
fleet with location, destination, travel times, health, speed and cargo). Region
shards also move to v4 so new and migrated saves share one schema number. v1, v2
and v3 are still accepted and migrated in memory; v5+ and format mismatches are
refused without touching the file. `slot.json` remains `spm-slot` v1 and purely
derived.

**Why.** M5 C0 must settle persistence before building content UIs. Rovers move
between region shards that are loaded on demand, so fleet state belongs in
`campaign.json`. Contracts and events are campaign-wide too. Science points and
unlocked techs already existed in v3 and stay top-level; no timed-research field
was invented before C1 defines the rules.

**Cost.** Old runtimes cannot read v4 saves, by design. Every supported older
version needs a fixture and an explicit migration path, and unknown enum values
or missing record ids need deterministic fallbacks. The bake fingerprint gate,
tile format and `slot.json` contract are untouched.

---

## D36 — Typed failure/repair records on installations

**Commit:** 2026-11-19 · **Status:** active

**Decision.** M5 failure and repair state is stored as a typed `failure` object on
each installation in its region shard: cause, severity, started time, optional
repair start and optional repair completion. It is omitted while healthy. The
existing primitive-only `state` bag remains available for future or unknown data
but is not used for gameplay-critical failure values.

**Why.** `health`, `status` and `statusReason` already exist, but they cannot
express when a failure happened or when a repair completes without inventing
string conventions. A typed field makes migrations and tests explicit while
leaving v1–v3 shards readable with a simple "no failure" default.

**Cost.** Region shards are rewritten as v4 on the next save even when their site
shape did not change. Unknown or invalid failure values are clamped/skipped
rather than corrupting the live campaign. M5 C4 keeps the record after a finished
repair with `repairCompleteGameHour` set, so the completion is observable without
a second save field; a later failure replaces it with a fresh record.

---

## D37 — Rover default speed is 8 km/h

**Commit:** 2026-11-19 · **Status:** active

**Decision.** New rovers default to `CampaignDefaults.DefaultRoverSpeedKmh = 8`.
The existing `--speed` CLI override remains available for automation and tuning.

**Why.** Plan §2.3 specified 8 km/h as the testing default while the route code
used 10 km/h. M5 C0 is the save-contract gate and the natural place to remove the
drift before rover fleet state is persisted.

**Cost.** Route-time previews in automated runs without `--speed` become ~25%
slower than in M4 builds. The stored `speedKmh` is authoritative once a rover
exists, so old fleets do not silently change.

---

## D38 — Static tech catalogue with immediate science purchases

**Commit:** 2026-11-19 · **Status:** active

**Decision.** `Sim/Campaign/TechCatalogue.cs` holds the seven tech ids already
referenced by `BuildingDef.RequiredTech`, each with a display name, tier,
science cost, prerequisites and the building ids it unlocks. Definitions are
static; the v4 save continues to store only `sciencePoints` and `unlockedTechs`.
`CampaignState.TryPurchaseTech` spends science immediately, checks prerequisites
and refuses unknown/duplicate/unaffordable techs. The `T` screen in
`ShellOverlay` renders live state and saves the campaign after a successful
purchase; `--tech` opens it for automation.

**Why.** C0 deliberately rejected timed research and a new save field, so C1
needed the smallest rule set that makes the existing `RequiredTech` gates
reachable from gameplay. A static catalogue is testable in `Sim`, keeps all
content in one place, and lets a test prove that no building can require an
unresearchable tech. The row-based immediate-mode screen matches the existing
menu/pause UI instead of introducing a new widget hierarchy.

**Cost.** Tech costs (40–150 science) and the three-tier prerequisite graph are
new design numbers, not derived from the original design docs; they are easy to
tune in one file. Saves that already contain an unlocked id outside the catalogue
keep working — placement still honours it — but the screen does not display it.
No schema, fingerprint or tile-format change was needed.

---

## D39 — AreaView now; rovers come from garages

**Commit:** 2026-11-19 · **Status:** active

**Decision.** M5 C2 builds the multi-region `AreaView` immediately rather than
deferring it. It renders the active region plus its present H3 neighbours on one
tangent plane, with region borders/hover outlines, rover markers and cross-region
tracks. The engine-free `RegionalRoutePlanner` runs A* over the coarse region graph
and reuses `RoutePlanner`, `MovementModel` and on-demand region tiles for fine-hex
legs. Rovers are **not** granted at campaign start: `CampaignState.TryPurchaseRover`
requires an operational, unpaused `rover_garage` and enforces its `rover_slots`
capacity; `DispatchRover` sends an available rover along a route and `Tick` applies
the arrival. The `R` fleet screen and `A` area key expose this loop; `--view=area`,
`--fleet` and `--dispatch` support automation. Cargo remains persisted and
displayed, but transfer economy stays out of C2.

**Why.** The user chose both "build AreaView now" and "garage purchase only" at the
C2 decision point. Cross-region movement needs a view between the globe and a
single region, and a fleet with no acquisition rule would either appear from
nothing or wait on a larger economy milestone. Reusing existing v4 `rovers` fields
and movement APIs keeps the save contract and deterministic core intact.

**Cost.** The area view loads up to seven region tiles and recomputes geometry on
entry; it deliberately does not cover the whole body. Coarse region A* chooses the
cheapest border crossing per pair but does not globally optimise every internal
hex path at once. Fleet rows are capped at eight in the immediate-mode screen.
No save-schema, fingerprint or tile-format change was needed.

---

## D40 — Area-first navigation

**Commit:** 2026-11-19 · **Reviewed:** 2026-11-20 · **Status:** done

**Decision.** The multi-region area view is the default drill-in from the globe:
clicking a body region or starting with `--region` lands in `AreaView`, and
clicking a region there recentres the area on it. `Enter` opens the centred
region's single-region view. The intended hierarchy is planet → area → region → site. `AreaView` keeps its flat
tangent-plane map but adds an **isometric projection** of all seven tiles as one
faceted slab, visually matching the colony diorama. `G` toggles flat ↔ isometric
in area view; colony view is reached from region view. Both projections support
the five region overlays — biome, terrain, site score, buildings and colour —
with `1`–`5` direct selection and `Tab` cycling. Terrain and site-score ramps are
normalised area-wide over all 2,401 hexes. `Q`/`E` rotate the isometric slab and
`[`/`]` adjust relief; `--projection=flat|iso`, `--rot` and `--relief` expose the
same paths to automation. `--slope` and `--overlay=terrain` still select the
terrain overlay. Explicit `--view=region|management|site|globe` flags still win.

**Why.** The area scale gives better situational awareness than jumping straight
to one region. The user clarified that the goal is not a slope-shaded flat map but
the colony-style isometric treatment at seven-region scale, with the same filters
the region view already offers. `AreaDiorama` reuses `IsoProjection`, the shared
corner-height approach and the palettes already used by `ManagementView`, so the
experiment adds no new view, save field or change to `Sim`. The flat presentation
is retained because it remains useful for rover logistics and because the toggle
makes the projection difference explicit.

**Cost.** The isometric mesh covers up to 2,401 fine hexes instead of 343. Corner
heights are averaged by shared string keys because `GetCellBoundaryVertices()`
returns `LatLng`, not an H3 index; only the outer edge of the seven-tile mosaic
gets a skirt. Site scores are computed lazily on first use (~300 ms on the real
Moon bake) and cached for the session; the steady-state frame budget stays under
the 20 ms gate. The presentation-only smoothstep relief curve from the first pass
is retained for readable slope contrast, and all palettes come from the existing
`RegionPalettes` helpers. No save-schema, fingerprint or tile-format change was
needed. Visual review on 2026-11-20 confirmed the seven-region diorama — the
Valles Marineris relief on Mars reads especially well — so the experiment is now
the default navigation path rather than a reversible trial.

**Recentre behaviour.** Selecting a region in `AreaView` now makes it the centre
and loads a new seven-region area around it. A rover click still dispatches when
a rover is selected. `Enter` in area view opens the centred region's single-region
view, preserving the planet → area → region → site ladder. The centred region's
yellow outline remains removed and the hover outline stays a subdued dark shade,
so neither marking reads as a highlight.

---

## D41 — Missions use the v4 shell and static definitions

**Commit:** 2026-11-20 · **Status:** active

**Decision.** M5 C3 puts all contract and event content in two static catalogues:
`ContractCatalogue` defines three delivery contracts with stockpile targets,
durations, credit/science rewards and a completion bonus; `EventCatalogue`
defines three weighted events with two choices each. `CampaignState` accepts,
delivers against and settles contracts, and raises/resolves seeded events from
the campaign RNG. Delivery is partial and proportional: any delivered fraction
pays that fraction of the credits and science on settlement, a fully met target
set adds the completion bonus, and a deadline closes the contract exactly once.
Events cannot start before hour 24, are spaced by a 72-hour cooldown, may pin a
visited region, and append to the bounded 50-entry log. `C` and `--missions`
open a shell screen driven by a provider; successful actions save immediately.

**Why.** C0 deliberately froze save v4 with the *fields* for contracts and events
but no definitions. Keeping rewards, text and cooldowns in code means gameplay
tuning never touches the schema, fingerprint or tile format. Proportional partial
payouts make an incomplete contract useful rather than a dead end, and the
deadline settlement keeps the v4 `RewardClaimed` flag authoritative so a claim
cannot pay twice.

**Cost.** Content changes require a code change and a new build rather than a
data file, and unknown definition ids in old saves are only displayed, not
resolved. Contract targets are single stockpile counters with no bandwidth or
rover-cargo transport requirement yet; event choices apply simple flat effects
rather than a condition system. The missions screen shows at most two offers and
two active contracts at a time, and only the first pending event. No save-schema,
fingerprint or tile-format change was needed.

---

## D42 — Failures are static causes with timed, paid repairs

**Commit:** 2026-11-20 · **Status:** active

**Decision.** M5 C4 adds `FailureCatalogue` with four static causes: each has a
severity range, repair hours and a credit/material bill. `TryDamageBuilding`
writes a typed v4 failure to an operational installation and sets `Damaged`, or
`Destroyed` at severity ≥ 0.85. `TryStartRepair` charges the severity-scaled bill
and records the start hour; `Tick` finishes the repair after the derived duration,
restores health and status, and leaves the completion hour on the record. An
active installation can also fail from a low per-hour hazard rolled from the
persisted RNG; paused ones are skipped. Event choices may pin one failure to the
event's region. `X` and `--maintenance` expose the loop in the shell.

**Why.** C0 froze the shard fields but not the rules. Keeping causes, bills and
repair times in a static catalogue means tuning never changes the save schema or
bake fingerprint, and deriving the duration from cause + severity means no ETA
field is needed. Severity-scaled partial damage makes a failure a setback with a
cost rather than a permanent loss, while the destroyed tier keeps the stakes
real without a dead end. Pausing is respected so the player's explicit choice is
never overridden by a random roll.

**Cost.** A repaired installation keeps its typed record with a completion hour,
so code must treat a non-null `Failure` as "has failed before" and use `Status`
to decide whether it is currently broken. Repairs occupy no crew or factory and
consume no bandwidth, so the loop is deliberately shallow. Only loaded regions
are hazarded or listed in the maintenance screen, matching the existing tick
limitation. No save-schema, fingerprint or tile-format change was needed.

---

## D43 — M5 closes on a reproducible verification gate

**Commit:** 2026-11-20 · **Status:** active

**Decision.** M5 C5 defines "done" as a clean solution build, 190/190 C# tests,
5/5 pipeline tests, 25/25 headless Godot frame-gate smokes at 20 ms p95, a clean
`git diff --check`, and docs/roadmap/decisions/plan snapshot in sync. CI runs the
same smoke matrix on synthetic Moon/Mars fixtures; local verification uses the
real baked data with scratch save roots. No save-schema, fingerprint or tile
format was changed, and nothing was pushed.

**Why.** C0–C4 added contracts, events, rovers, tech and failures without
changing the v4 save contract, so the drift risk now lives in documentation and
automation rather than in persisted state. One gate catches stale test counts,
missing CLI flags and view regressions before Earth and the LOD experiment branch
away from `main`.

**Cost.** Full local verification takes a few minutes and needs both Godot 4.7.2
mono and a Python 3.13 pipeline environment; CI downloads Godot and pins its
SHA-256. The matrix is deliberately broad (25 runs across both bodies and every
shell screen) instead of minimal, so adding a view requires adding its smoke.

---

## D44 — Earth is a synthetic, water-aware playable sandbox

**Date:** 2026-11-20 · **Status:** active

**Decision.** Earth becomes the third baked, playable body and the launch default
when `data/earth` exists. Water presence and class stay in the authoritative
biome byte of `fine.bin`; `TileFormat` remains v1 and the v4 save schema gains no
fields. Placement is forbidden on water unless a `BuildingDef` opts in with
`buildOnWater`, so the offshore pad is available from the start while ordinary
buildings keep their existing rules. Movement gains `TerrainMode`; barges are
water rovers purchased at a new barge port alongside the rover garage. Earth uses
the shared building catalogue with translated biomes and baseline starting tech.

River detail lives in an optional presentation-only `fluid.bin`/`fluid.json`
sidecar. The runtime validates body, resolution, hex count, length and SHA-256,
then draws flat water fills and river strokes in region, area and colony views;
missing or corrupt sidecars warn and fall back to biome colours. The globe uses
region-mean colours and the site grid is unchanged. Only deterministic synthetic
Earth imagery and fluid fixtures ship: real data URLs and checksums are still
unverified, so non-synthetic Earth sidecar bakes fail closed rather than guess a
source. Historical Earth scenarios are deferred.

**Why.** The user wants a full homeworld testing sandbox before the separate
seamless-H3-LOD experiment. Keeping water authoritative in Sim and flow detail
out of the save means the new presentation cannot weaken placement, routing or
bake-fingerprint rules. A synthetic fixture gives CI reproducible globe/region/
area/colony/fleet coverage without committing `data/`.

**Cost.** Earth adds seven frame-gate smokes (32 total), a third synthetic tile
bake and two more sidecar bakes to CI, plus Python fluid tests. Local verification
is clean build (0 warnings/errors), 209/209 C#, 10/10 pipeline tests and 32/32
headless Godot smokes PASS (worst p95 8.63 ms against the 20 ms gate). No save
schema, fingerprint rule or `slot.json` contract changed; nothing was committed
or pushed by this work. The fixture's visual shape was later refined by D45; the
gameplay contract above is unchanged. Seamless H3 LOD remains a separate follow-up.

---

## D45 — Synthetic Earth reads like a planet

**Date:** 2026-11-20 · **Status:** active

**Decision.** Replace the eight-ellipse Earth fixture with a deterministic
warped fBm surface in `pipeline/spm/synthetic_earth.py`. Continent anchors supply
the broad arrangement, domain warping and multi-octave detail supply irregular
coasts, and explicit seeds preserve mid-sized islands (British Isles, Japan,
Philippines, Indonesia, Madagascar, Sri Lanka, New Zealand and Tasmania). Africa
and Eurasia meet through Arabia/Sinai. Biome and elevation are sampled at every
res-6 hex centroid; imagery reuses the same field, so colour, biome and flow
agree. Removed the independent per-hex ordinal noise that made all terrain
spiky; rivers meander, lake basins are continuous depressions and mountains are
broad belts. Region dominant biomes still come from `hexify.region_dominant`, and
the globe still draws 41,162 region hexes with per-region means.

`ReliefMath` gives Earth a 2,500 m elevation-span floor before the terrain views
normalise colour and geometry. Flat ocean and lowland regions therefore stay
flat, while Moon/Mars keep their existing local-span behaviour; water remains
flattened in every renderer. H3 fine res 6 / region res 3, `TileFormat` v1, the
v4 save schema, water placement rules and the fluid sidecar format are unchanged.
The CI smoke regions were re-derived from the final bake so they still cover a
mixed coast, a lake and a river.

**Why.** Playtest showed the original synthetic Earth as two touching ellipses,
a blobby Australia, missing South Asian islands and spikes everywhere. Bumping
H3 resolution would multiply hex counts roughly 7× per step and still not fix
the field; per-hex sampling of a continuous field fixes the visual cause while
keeping the shipped runtime layout and gameplay intact.

**Cost.** Earth tile and imagery bakes take about two minutes each locally
instead of using one value per region; memory stays bounded by 64-region batches.
Verification: clean build (0 warnings/errors), 209/209 C#, 17/17 pipeline tests
(7 new Earth field tests), 32/32 headless Godot smokes PASS (worst p95 7.14 ms
against the 20 ms gate), `git diff --check` clean. No save schema, fingerprint
rule or `slot.json` contract changed; nothing was committed or pushed.

---

## D46 — Real Earth geography replaces the synthetic fixture

**Date:** 2026-11-22 · **Status:** active

**Decision.** Bake Earth from pinned public-domain sources instead of the
procedural field introduced by D44/D45:

- Natural Earth 1:10m land, lake and river-centreline vectors for the
  authoritative coastlines and water biome bytes;
- NOAA ETOPO1 1-arcminute Ice-surface grid-registered relief for land elevation
  and bathymetry;
- NASA Blue Marble Next Generation (December 2004, 21600×10800, with
  topography and bathymetry) for the `color.bin` sidecar and as a land-cover
  proxy (greenness plus latitude/elevation selects forest, taiga, tundra,
  hills, plains and mountains).

All five inputs are pinned by SHA-256 in `pipeline/spm/fetch.py` and cached
under `pipeline/.cache/earth/`. `pipeline/spm/earth_real.py` rasterises the
vectors and samples every res-6 hex centroid in bounded batches, so biome,
elevation and colour describe the same real geography. `--synthetic` still
produces the deterministic D45 CI fixture (identical tile and colour bytes);
CI is unchanged. The
`earth.volcanic` biome is retained in the table but is not assigned by the real
path, and the globe still resolves one mean colour per res-3 region.

Validating the real bake exposed a pre-existing block-alignment bug in
`imagery.aggregate_to_hex`: each 512-row block restarted at 90°N and used
block-local row spacing, so later source rows were painted onto northern hexes.
`raster.global_latlon` now takes an explicit `dlat` for sliced grids, a
regression test guards the alignment, and all three real colour sidecars were
rebaked. Moon and Mars changed only in `color.bin`/`imagery.json`; their tile
manifests and save fingerprints are untouched.

**Why.** D45 fixed the *shape* of the procedural blobs but Earth remained the
only body with no real data: Moon and Mars bake real DEMs, geology and pinned
imagery mosaics. A smooth procedural field averaged over ~60 km region cells
cannot produce recognisable coasts, islands or mountain belts. Real sources are
the only way for Earth to read like the other two bodies. After the rebake,
spot checks match the source imagery at Sahara, Congo, Antarctica, Australia,
India and the Himalaya, and ETOPO at Everest and the Mariana Trench, so the
globe's existing per-region averaging stays unchanged.

**Cost.** A real Earth bake downloads ~430 MB of pinned sources (ETOPO1 alone is
376 MiB gzipped and unpacks to 890 MiB) and takes substantially longer than the
synthetic fixture. The new `generatedUtc` changes Earth's bake fingerprint, so
pre-existing Earth save slots are refused by the gate and never overwritten;
fresh slots are required. No save schema, `TileFormat` version, water-placement
rule or `slot.json` contract changed. Verification: see the status snapshot in
`roadmap.md`.

---

## D47 — Correct globe handedness and coastal water sites

**Date:** 2026-11-22 · **Status:** active

**Decision.** Flip only the globe's screen-space X axis in `MoonGlobe.Rotate`
and apply a building's water permission to the site's centre hex, not to every
hex in the site-score neighbourhood.

**Why.** Sampling `data/earth` at known lat/lon already returned the right
geography, so the east/west reversal was in the shared globe projection: with
the camera looking down `+z1`, the old `x1` screen coordinate grew westward.
Negating it makes west/left and east/right agree with maps while leaving pitch,
north/south and all picking paths unchanged.

A site parent is one fine hex, so it is never partly land and partly water. The
barge port is water-required with a single-hex footprint, but `ScoreSite`
applied `WaterPlacement.Required` to the radius-1 scoring neighbourhood too.
That rejected a coastal water hex whenever a neighbour was land, producing the
catch-22 where land was rejected as land and coastal water as "not buildable".
Neighbour hexes only need to be buildable terrain now; real multi-hex footprints
still enforce their full water rule in `BuildingDef.CanPlaceAt`.

**Cost.** No bake, save schema, fingerprint or CI fixture changes. Moon and Mars
globes are also unmirrored by the shared projection fix, which is correct for
their source imagery. Verification: 212/212 C#, 22/22 pipeline, 32/32 headless
smokes; a barge port now places on open ocean and coastal water hexes, still
fails on land, and multi-hex water footprints still reject mixed land/water.

---

## D48 — Seam-free globe from real H3 boundaries

**Date:** 2026-11-22 · **Status:** active

**Decision.** Stop drawing the globe as one flat regular hexagon per res-3
region. `MoonGlobe` now bakes every region's H3 boundary vertices into a single
static `ArrayMesh` and rotates that mesh in `Shaders/GlobeCells.gdshader`. Region
colours remain per-vertex, so imagery/biome toggles rebuild the mesh once with
new colours; camera changes update four shader uniforms. Fragments with rotated
`z <= 0` are discarded, giving exact orthographic limb clipping.

**Why.** A flat tangent hex placed at a projected centroid does not share edges
with its neighbours. The old `SeamFudge = 1.04` hid most small cracks but could
never make the tiling continuous, especially near the limb or on pentagons. H3
already returns shared boundary vectors for every cell and `MoonGlobe` already
cached them for hover outlines; using the same vertices as filled geometry makes
adjacent cells abut exactly. This is body-agnostic: Moon, Mars, Earth and future
bakes all carry H3 region boundaries, so no per-body rule or pipeline change is
needed.

**Cost.** One new shader resource and a one-time mesh build at globe
initialisation. Picking still uses projected centroids, so hover/select behaviour
is unchanged. No bake, save schema, fingerprint, manifest or CI-fixture change.
Verification: clean build, 212/212 C#, 22/22 pipeline, 32/32 headless smokes
(worst p95 7.14 ms); a non-headless Earth capture has zero background pixels
inside the globe disc, so no seams remain.

**Follow-up.** The first D48 build passed `BuildCellMesh()` straight to the
`MeshInstance2D` without assigning `_cellMesh`, so `ApplyCellColors()` returned
early and the globe stayed in biome colours despite a correct imagery state.
Mesh construction now goes through `ApplyCellColors()` itself, which logs the
colour source it baked; CI greps that line in the globe smokes so a silent
fallback to biome colours fails the build.

---

## D49 — Seamless H3 LOD experiment: REVISE, do not port

**Status.** Experiment branch `experiment/seamless-h3-lod` only. No save schema,
bake manifest, fingerprint or `Sim` change; nothing ports to `main` under this
decision.

**Verdict: REVISE.** Keep the mechanism, reject the current presentation as
production-ready.

**Evidence.** Stage A built three tiers from one Res 3 parent
(`1 / 343 / 2401` cells) with a 220 ms crossfade; 300-frame p95 6.94 ms. Stage B
drew the focus parent at the zoom tier and its `gridDisk(1)` neighbours one tier
coarser, with a 16-entry LRU and one shared gnomonic basis; a 1,200-frame
pan+zoom stress logged two full `0->1->2->0` cycles at p95 6.94 ms, max tile
load 21 ms at Res 7, 333 loads / 271 evictions, and working set 276-306 MB with
no growth. Full verification: clean build, 212/212 C#, 22/22 pipeline, 32/32
headless smokes (worst p95 10.01 ms). Shared H3 boundary vertices projected
through one basis leave no gap between mixed-tier parents; a central 200x200 px
box contained zero background pixels.

**Why revise, not keep.** Res 6/7 are visual-only: their fills are deterministic
brightness variants of the parent biome, so the detail tier has no gameplay or
geography behind it. The gnomonic anchor is fixed, so far panning distorts and
will need reprojection. VRAM was not measured, the probe overlays the normal
views (so timings include a constant baseline), and the LRU holds only a
seven-parent window rather than a globe-wide tile set.

**Why not kill.** Crossfade, mixed-tier seams, tile latency and memory all meet
the brief's gates on the target-class machine, so the core approach is sound and
worth one more iteration.

**Next iteration.** Add real Res 6/7 biome/relief samples, reprojection or a
globe camera, VRAM instrumentation and a no-baseline frame measurement; then
write a new decision before any port to `main`. Details in
[`lod-experiment.md`](lod-experiment.md). D50 implements the presentation part
of that iteration; the fidelity and measurement gaps remain.

---

## D50 — Five-tier LOD refinement: presentation fixed, still REVISE

**Status.** Experiment branch `experiment/seamless-h3-lod` only. No save schema,
bake manifest, fingerprint or `Sim` change; nothing ports to `main`.

**Change.** The three-tier probe becomes Res 3/4/5/6/7. Zoom thresholds are
spaced geometrically across 1–128 with 5% down-hysteresis, so the density step
between neighbouring rings is always 7× instead of the original 343× first jump.
A tier-dependent core radius (Res 4/5: 4, Res 6: 2, Res 7: 1) keeps rebuilds
inside the 100 ms tile budget; one outer ring is one tier coarser and a 96-parent
LRU holds the window. Builds are queued against an 8 ms per-frame budget.

The cross-dissolve is replaced by an opaque coarse underlay with the finer mesh
fading over it, and fine-tier brightness variation is reduced from ±14% to ±5%.
Interrupted fades continue from the visually dominant mesh. These changes target
the user's observation that the centre tile visibly gained detail over its
neighbours.

**Evidence.** A final 1,200-frame pan+zoom stress logged two complete
`0→1→2→3→4→0` cycles at `avg 8.52 ms · p95 16.67 ms · limit 20 ms · PASS`
(an earlier identical run measured 14.07 ms p95), with a peak build queue of 23
parents and max tile load 27 ms headless (35–44 ms for non-headless initial Res 7
builds). Working set stayed 276–313 MB headless with no growth across 2,220 loads
/ 1,503 evictions. Held 50% crossfades at zoom
12/20/60 have zero background pixels in the central 200×200 box and mean
luminance within 0.5% of the settled tier, so the mid-fade dimming is gone.
Full verification: clean build, 212/212 C#, 22/22 pipeline, 32/32 headless
smokes (worst p95 7.14 ms); `git diff --check` clean.

**Still revise.** The extra tiers do not add geography: Res 4–7 remain
deterministic brightness variants of one Res 3 biome colour. The gnomonic anchor
is still fixed, VRAM is still unmeasured, and probe timings still include the
normal views behind it. Larger windows also raised p95 from 6.94 ms to 14–17 ms;
it passes the 20 ms stretch gate but leaves less headroom on slower hardware.

**Next.** Port only after a separate decision that adds real fine detail samples,
reprojection or a globe camera, VRAM instrumentation and no-baseline frame
measurement. Details in [`lod-experiment.md`](lod-experiment.md).

---

## Open decisions

Tracked in [`roadmap.md`](roadmap.md#open-questions): bake-change save migration,
hover-only region outlines, and 3D building assets/presentation. Multi-installation
hexes, the site/colony split and the local grid size are settled by D20–D22;
tech/cost enforcement and time scale are settled by D18 and D17; the slot metadata
layout is settled by D15. M4 body, catalogue, save-isolation, frame-gate and CI
choices are settled by D23–D29, M4.5 imagery by D30–D32, terrain presentation by
D33–D34, the M5 C0 save contract, typed failures and rover speed by D35–D37, the
M5 C1 tech catalogue and purchase rule by D38, the M5 C2 AreaView/garage choice
by D39, the area-first navigation/slope test by D40, the M5 C3 static-catalogue
mission rule by D41, the M5 C4 static-cause repair loop by D42, the M5 C5
verification gate by D43, the synthetic Earth/water presentation sandbox by D44,
and the organic per-hex Earth surface by D45. Globe handedness and coastal water
sites are settled by D47; the seam-free boundary mesh by D48. The seamless H3
LOD experiment remains on REVISE under D49 and D50 until fidelity, reprojection,
VRAM and isolated timing gaps are closed.
