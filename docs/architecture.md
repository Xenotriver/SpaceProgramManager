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
   for buildings. Views (`RegionView`, `ManagementView`) are renderers that read
   it and route mutations through it.
4. **Saves are guarded.** A campaign is tied to the exact bake it was created
   against, written atomically, and never silently overwritten when unreadable.

## 2. Layers and dependencies

```
┌──────────────────────────────────────────────────────────────┐
│ Game (Godot 4.7.2, net8.0)                                   │
│  Main · MoonGlobe · RegionView · ManagementView · CampaignService │
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
| `Sim/Site` | `SiteScore`, `SiteFactors`, `SiteScorerConfig`, `RegionTileScoring` (sunlight, comms, terrain, resources, crater clearance) |
| `Sim/Buildings` | `BuildingDef` (17 definitions in `MoonBuildings`), `BuildingRegistry`, `BuildingInstance`, `BuildingStatus`, placement validation |
| `Sim/Campaign` | `CampaignState`, `RegionState`, `CampaignMeta`, `CampaignStore`, `BakeFingerprint` |

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
| `Main` | Mode state machine (`Globe` / `Region` / `Management`), HUD, input dispatch, CLI parsing, owns `BuildingRegistry`, `CampaignState`, `CampaignService` |
| `MoonGlobe` | Renders all 41,162 region polygons as a `MultiMesh`; hover/select, yaw/pitch/zoom, pan |
| `RegionView` | One region's 343 hexes in a gnomonic tangent plane; four overlays, hover/select, rover route, building ghost/placement/removal |
| `ManagementView` | Isometric diorama of the same region: one painter-sorted `ArrayMesh`, relief, overlays, pan/zoom/pick, 60° orbit, placement/removal |
| `CampaignService` | Resolves the save slot, gates on the bake fingerprint, loads shards before a view, saves immediately on change, quarantines corrupt shards, reports status |
| `IsoProjection` | Pure helpers for the isometric projection, inverse projection, painter depth and plane rotation |

## 5. Data flow

### Input → state → disk

```
mouse/keyboard
   → Main input dispatch
   → RegionView / ManagementView hit-testing
   → CampaignState.Preview / TryPlace / TryRemove / Clear
   → CampaignState.RegionChanged
   → CampaignService: CampaignStore.SaveRegion + SaveMeta
   → <slot>/regions/<h3>.json + <slot>/campaign.json
```

The views never mutate `RegionState` directly; they call campaign methods and then
rebuild their cached geometry.

### Disk → state → view

```
CampaignService.Load()
   → CampaignStore.LoadMeta(fingerprint)
   → CampaignState.RestoreMeta(meta)
CampaignService.EnsureRegionLoaded(regionIndex)
   → CampaignStore.LoadRegion(regionIndex)
   → CampaignState.InstallRegion(stored, tile)   # rebuild footprints, drop invalid records
RegionView.Load(world, regionIndex)              # must run after EnsureRegionLoaded
ManagementView.Load(world, regionIndex)
```

## 6. Coordinate systems

### H3

- Region: resolution 3 (~60 km across), 41,162 on the Moon.
- Fine hex: resolution 6 (~0.9 km edge), 343 per region (286 on pentagons).
- `RegionTile.Cells` is in H3 child order, matching the Python pipeline; the
  order is covered by `ChildOrderTests`.

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

Picking in both 2D views is nearest projected cell centre within a radius, not
polygon containment.

## 7. Rendering and performance

- **Globe** — one `MultiMesh` instance per region, plus a boundary polyline for the
  hovered/selected region. 41k instances are uploaded once; pan/rotate updates the
  instance transforms.
- **Region view** — fills are batched into an `ArrayMesh` (rebuilt when the overlay
  changes); outlines, ghost and route are drawn per frame. Site scores are computed
  once per load and cached.
- **Colony view** — the entire terrain is a single `ArrayMesh` with painter-sorted
  triangles, so the region costs one mesh draw; overlays only swap vertex colours
  (mesh rebuild). Orbit rebuilds geometry arrays once per step.
- `--fps` prints frame time plus per-view draw timings (`LastDrawMs`,
  `AverageDrawMs`), and `--redraw` forces continuous redraws for benchmarking.

## 8. Extension points

| Change | Where to start |
|---|---|
| New body | `pipeline/spm/bodies.py`, bake, then `BodyWorld.Open`; `Main.OpenMoon()` is currently hardcoded |
| New overlay | `RegionViewMode`, palette in `RegionPalettes`, fill computation in `RegionView`/`ManagementView` |
| New building | Add a `BuildingDef` to `MoonBuildings`, keep `MoonBuildings_AreConsistent` green |
| Simulation tick | New `Sim/Time` project folder; `CampaignState.Tick(hours)`; drive from `Main._Process` |
| Site view | New view over one hex; reuse `IsoProjection` and the campaign shard |
| Save migration | `CampaignStore.Version` + `EnsureSupported` + migration tests |

## 9. Known constraints

- Single body, single open region, no simulation clock.
- Building footprints may not overlap, although the design intends multiple
  installations per hex (plan §2.5). The per-hex site view is where that is meant
  to be resolved.
- `RequiredTech` is displayed but not enforced at placement; `Cost` is data only.
- `Main.cs` (~1,050 lines) owns most UI and input; a shell/menu split is planned
  in M2.
- No CI, analyzers, or automated screenshot regression yet.
