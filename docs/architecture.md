# Architecture

How the pipeline, the simulation core, the Godot runtime, and persistence fit
together. For *what is planned next*, see [`roadmap.md`](roadmap.md); for the
save contract, see [`save-format.md`](save-format.md).

## 1. Principles

1. **`Sim` is engine-free.** No Godot types, no file-system side effects beyond
   the tile/save loaders, no rendering. Everything deterministic and unit-testable.
2. **The pipeline bakes, the runtime loads.** Heavy geometry, biome classification
   and crater detection happen offline in Python. The runtime reads compact tiles.
3. **Campaign state has one owner.** `CampaignState` is the single source of truth
   for installations. `SiteView` is the only view that routes placement/removal
   mutations into it; `RegionView` and `ManagementView` are read-only inspectors.
4. **Saves are guarded.** A campaign is tied to the exact bake it was created
   against, written atomically, and never silently overwritten when unreadable.

## 2. Layers and dependencies

```
┌──────────────────────────────────────────────────────────────┐
│ Game (Godot 4.7.2, net8.0)                                   │
│  Main · MoonGlobe · RegionView · ManagementView · SiteView     │
│  ShellOverlay · CampaignService · GameSettings                 │
└───────────────────────────────┬──────────────────────────────┘
                                │ ProjectReference
┌───────────────────────────────▼──────────────────────────────┐
│ Sim (net8.0, pocketken.H3 4.5.0.1)                           │
│  Hex · Biomes · Movement · World · Site · Buildings · Campaign │
└───────────────────────────────┬──────────────────────────────┘
                                │ reads
┌───────────────────────────────▼──────────────────────────────┐
│ data/<body>/manifest.json + fine.bin                         │
└───────────────────────────────▲──────────────────────────────┘
                                │ writes
┌───────────────────────────────┴──────────────────────────────┐
│ pipeline/ (Python 3.13: h3, rasterio, numpy, geopandas)      │
└──────────────────────────────────────────────────────────────┘
```

Projects: `SpaceProgramManager.slnx` → `Sim`, `Sim.Tests`, `Game`.

## 3. Sim modules

| Module | Responsibility |
|---|---|
| `Sim/Hex` | `HexGrid`: H3 index ↔ lat/long, neighbours, parent/children, distance |
| `Sim/Biomes` | `Biome`, `DominantBiomeResolver` (area share → neighbour majority → distance → priority) |
| `Sim/Movement` | `MovementModel` (uphill ×0.8, downhill ×1.15, crater penalty, no stacking), `RoutePlanner` (cheapest-time path) |
| `Sim/World` | `BodyManifest`, `BodyWorld`, `RegionTile`, `HexRecord`, `TileFormat`, `BiomeRegistry` |
| `Sim/Site` | `LocalGrid` (37-cell axial disk), `SiteScore`, `SiteFactors`, `SiteScorerConfig`, `RegionTileScoring` (sunlight, comms, terrain, resources, crater clearance) |
| `Sim/Buildings` | `BuildingDef` (17 definitions in `MoonBuildings`), `BuildingRegistry`, `BuildingInstance` (`ParentHex` + `LocalCenter`), `BuildingStatus`, local placement validation |
| `Sim/Campaign` | `CampaignState`, `RegionState`, `SiteState`, `CampaignMeta`, `CampaignStore`, `CampaignSlotStore`, `SlotSummary`, `BakeFingerprint` |

### World loading

`BodyWorld.Open(bodyKey, dataRoot)` finds the data root (explicit path, `SPM_DATA`,
or upward search), loads `manifest.json`, validates it, and keeps `fine.bin` open
via a `RandomAccess` handle. `GetRegion(index)` slices the region's bytes and
builds a `RegionTile` on demand. Only one region tile is held at a time by the
runtime.

### Tile format (v1)

`manifest.json` lists every region with `index`, `dominantBiome`, `hexCount`,
`offset`, `length`. Each tile is a 20-byte header (`SPMF` magic, version,
resolutions, hex count) followed by 4-byte records: biome `u8`, elevation `i16`
little-endian, crater `u8`. Pentagons have 286 children instead of 343.

## 4. Runtime modules

| Class | Role |
|---|---|
| `Main` | Mode state machine (`Globe` / `Region` / `Management` / `Site`), HUD, input dispatch, CLI parsing, owns `BuildingRegistry`, `CampaignState`, `CampaignService` |
| `MoonGlobe` | Renders all 41,162 region polygons as a `MultiMesh`; hover/select, yaw/pitch/zoom, pan |
| `RegionView` | One region's 343 hexes in a gnomonic tangent plane; four overlays, hover/select, rover route, aggregate site markers (read-only) |
| `ManagementView` | Isometric diorama of the same region: one painter-sorted `ArrayMesh`, relief, overlays, pan/zoom/pick, 60° orbit, aggregate site markers (read-only) |
| `SiteView` | One fine hex as a 37-cell isometric local grid; nearest-centre picking, footprint ghost, placement/removal via `CampaignState`; reuses `IsoProjection` |
| `CampaignService` | Binds the campaign to one save slot: gates on the bake fingerprint, loads shards before a view, saves immediately on change, keeps `slot.json` current, quarantines corrupt shards, reports status |
| `GameSettings` | `user://settings.json`: save root, last slot, relief default, FPS flag. Never inside a slot |
| `ShellOverlay` | Immediate-mode front end: main menu, slot list, pause, settings, search and toasts. Owns no game state; emits `Command` to `Main` and takes exclusive input while open |
| `IsoProjection` | Pure helpers for the isometric projection, inverse projection, painter depth and plane rotation |

### Startup and slot flow

```
launch
  → GameSettings.Load()                 # user://settings.json
  → BodyWorld.Open("moon")
  → no user args (or --menu)  → ShellOverlay main menu
      Continue / Play slot    → store pending slot, ReloadCurrentScene
  → any other user arg        → StartCampaign(slot) directly (automation)
StartCampaign(slotId)
  → CampaignService.Load()              # campaign.json through the fingerprint gate
  → slot.json view snapshot            # restore mode, region, camera, selected site hex
  → RegionView / ManagementView load the region on demand
  → SiteView loads the selected fine hex when mode is "site"
```

Slot switching, returning to the main menu and starting a new campaign all use
`SceneTree.ReloadCurrentScene()`. `_ExitTree` flushes the current slot and writes
its view snapshot; static pending-slot flags are read by the new scene. One
campaign service therefore lives per scene, and no half-swapped state exists.

## 5. Data flow

### Input → state → disk

```
mouse/keyboard
   → Main input dispatch
   → SiteView hit-testing (local grid cell)       # only in site mode
   → CampaignState.Preview / TryPlace / TryRemove(parentHex, localCell)
   → CampaignState.RegionChanged
   → CampaignService: CampaignStore.SaveRegion + SaveMeta + SaveSummary
   → <slot>/regions/<h3>.json + <slot>/campaign.json + <slot>/slot.json
```

`RegionView` and `ManagementView` only hit-test for selection and route planning;
they never mutate `RegionState`. `SiteView` calls campaign methods and then
refreshes its cached installation list.

### Disk → state → view

```
CampaignSlotStore.List()
   → CampaignStore.TryReadHeader()        # metadata only, never opens a slot
   → CampaignStore.TryReadSummary()       # slot.json cache, rebuilt when stale
CampaignService.Load()
   → CampaignStore.LoadMeta(fingerprint)
   → CampaignState.RestoreMeta(meta)
CampaignService.EnsureRegionLoaded(regionIndex)
   → CampaignStore.LoadRegion(regionIndex)
   → CampaignState.InstallRegion(stored, tile)   # rebuild local footprints, drop invalid records
RegionView.Load(world, regionIndex)              # must run after EnsureRegionLoaded
ManagementView.Load(world, regionIndex)
SiteView.Load(world, tile, hexOrdinal, campaign, registry)  # site mode only
```

## 6. Coordinate systems

### H3

- Region: resolution 3 (~60 km across), 41,162 on the Moon.
- Fine hex: resolution 6 (~0.9 km edge), 343 per region (286 on pentagons).
- `RegionTile.Cells` is in H3 child order, matching the Python pipeline; the
  order is covered by `ChildOrderTests`.

### Local site grid

Inside a site, cells are axial `(q, r)` in the disk
`max(|q|, |r|, |q + r|) <= 3` — 37 cells. `LocalGrid.IsInside` is the only
boundary authority; `LocalGrid.Footprint` expands a centre and radius, and
`CampaignState.Preview` rejects footprints that leave the disk or clash with an
existing installation in the same site. The disk is independent of H3 geometry:
a fine hex is the container, not a footprint cell.

### Region tangent plane (2D views)

The region is projected gnomonic-style onto the tangent plane at the region
centre: `plane = (v·east, v·north) / (v·centre)`, in radians. Screen mapping is
`center + pan + (x·scale, -y·scale)`. Elevation is converted to plane units by
dividing by the body radius before being used by the isometric view.

### Isometric projection (colony view)

```csharp
Project(plane, z) = ((x - y) * cos30, -(x + y) * sin30 - z)
Unproject(iso, z) = inverse of the above
Depth(plane)      = x + y        // larger = farther, drawn first
Rotate(plane, pivot, cos, sin)   // orbit about a pivot in plane space
```

A 60° orbit maps the hex lattice onto itself, so prisms keep their shape and only
the terrain silhouette changes. Rotation is applied to centres and corners before
projection; the depth sort and wall normals use the rotated plane.

`SiteView` reuses these helpers for its 37 local cells; it does not add a second
isometric engine. Picking in the region, colony and site views is nearest
projected cell centre within a radius, not polygon containment.

## 7. Rendering and performance

- **Globe** — one `MultiMesh` instance per region, plus a boundary polyline for the
  hovered/selected region. 41k instances are uploaded once; pan/rotate updates the
  instance transforms.
- **Region view** — fills are batched into an `ArrayMesh` (rebuilt when the overlay
  changes); outlines, site markers and route are drawn per frame. Site scores are
  computed once per load and cached.
- **Colony view** — the entire terrain is a single `ArrayMesh` with painter-sorted
  triangles, so the region costs one mesh draw; overlays only swap vertex colours
  (mesh rebuild). Orbit rebuilds geometry arrays once per step. Aggregate site
  markers are drawn on top.
- **Site view** — 37 local tiles in one small `ArrayMesh` with painter-sorted
  tops/walls; installation markers, hover/selection outlines and the footprint
  ghost are drawn per frame using `IsoProjection`.
- `--fps` prints frame time plus per-view draw timings (`LastDrawMs`,
  `AverageDrawMs`), and `--redraw` forces continuous redraws for benchmarking.

## 8. Extension points

| Change | Where to start |
|---|---|
| New body | `pipeline/spm/bodies.py`, bake, then `BodyWorld.Open`; `Main.OpenMoon()` is currently hardcoded |
| New overlay | `RegionViewMode`, palette in `RegionPalettes`, fill computation in `RegionView`/`ManagementView` |
| New building | Add a `BuildingDef` to `MoonBuildings`, keep `MoonBuildings_AreConsistent` green |
| Simulation tick | `Sim/Time/SimulationClock.cs` + `CampaignState.Tick(hours)`; already driven from `Main._Process` |
| Site grid size | `Sim/Site/LocalGrid.cs`; footprint validation in `CampaignState.Preview` |
| Site view | `Game/Scripts/SiteView.cs`; reuses `IsoProjection` and the campaign shard |
| Save migration | `CampaignStore.Version` + `EnsureSupported` + migration tests |

## 9. Known constraints

- Single body and one open region; the clock now runs, but `CampaignState.Tick`
  only simulates regions installed in memory so the globe stays cheap.
- Installations may share a fine hex only if their local footprints are disjoint
  inside its 37-cell site grid (plan §2.5, resolved in M3). H3-scale footprints
  are no longer used for placement.
- `RequiredTech` and `Cost` are enforced at placement; the tech *selection* UI is
  still M5, so `UnlockTech` is currently only reachable from code/tests.
- Building models are still procedural prisms. 3D assets and the fixed 2.5D art
  pipeline remain deferred (roadmap questions 11–12).
- `Main.cs` owns world HUD/input, simulation clock, mode wiring and startup;
  `ShellOverlay` owns the menu/pause/time/settings screens since M2. Further
  screen splitting remains deferred.
- No CI, analyzers, or automated screenshot regression yet.
