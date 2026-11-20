# Development

Build, test, automate, and bake. For design context read
[`architecture.md`](architecture.md); for what to build next read
[`roadmap.md`](roadmap.md).

## 1. Requirements

| Tool | Version | Notes |
|---|---|---|
| Godot | **4.7.2 .NET (mono) build** | The standard build cannot run C#. Steam/Epic builds are the standard build. |
| .NET SDK | **8.0+** | All projects target `net8.0`. |
| Python | **3.13** | Pipeline only. `h3` has no 3.15 wheel yet. |
| GPU | Vulkan 1.0 capable | Godot 4.7 forward+ renderer. |

Machine-specific Godot path used in this repo's examples:

```powershell
$godot = 'D:\LTC Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe'
```

## 2. Build and test

```powershell
dotnet build SpaceProgramManager.slnx
dotnet test Sim.Tests/Sim.Tests.csproj
```

Expected: `powodzenie: 190` — 190 passed, 0 failed, 0 skipped on a machine with
`data/moon` and `data/mars` present. Tests that need a real bake **skip** when it
is absent — do not make them fail. Set `SPM_REQUIRE_DATA=1` to turn those skips
into failures (intended for CI).

`dotnet build` builds `Sim`, `Sim.Tests`, and `Game`. Building `Game` requires the
Godot .NET SDK (`Godot.NET.Sdk/4.7.2`) to be resolvable, which the mono editor
installs.

### Test inventory

| File | Tests | Covers |
|---|---:|---|
| `BuildingTests.cs` | 20 | Registry consistency, placement rejection paths, footprint shape, tech filtering, per-body catalogues, Mars biome restrictions, real-bake biome ids |
| `CampaignStoreTests.cs` | 31 | Meta/shard round-trip, v1 meta + v1/v2/v3 → v4 migration, v4 contracts/events/rovers + typed failure round-trip, fingerprint + cross-body + version + format rejection, backups, corrupt quarantine, missing-shard delete, unknown ids, state value filtering, site view state |
| `CampaignSlotTests.cs` | 16 | Slot discovery/ordering, `slot.json` creation + rebuild, fingerprint/cross-body/unreadable states, delete safety, view-state round-trip |
| `CampaignTests.cs` | 16 | Site-scoped place/remove/clear, local preview footprint, two installations in one hex, independent sites, local overlap/clipping, install-from-save validation, visited tracking, change events |
| `ChildOrderTests.cs` | 2 | H3 child enumeration order matches h3-py (343 and 286 cells) |
| `DominantBiomeResolverTests.cs` | 7 | Area share, neighbour majority, distance fallback, priority order |
| `HexGridTests.cs` | 5 | H3 index ↔ lat/long round-trip, 6 neighbours on a normal cell, region parent/children, canonical H3 example |
| `KnownBodiesTests.cs` | 5 | Ready bodies are Moon + Mars; case-insensitive normalization rejects unknown keys |
| `LocalGridTests.cs` | 7 | 37-cell disk, centre-first order, boundary rejection, axial distance, local footprint sizes and ring-3 clipping |
| `MovementModelTests.cs` | 6 | Uphill/downhill multipliers, crater penalties, non-stacking rule |
| `RoutePlannerTests.cs` | 6 | Cheapest-time path, crater on destination, unreachable goal, empty route, leg totals |
| `SimulationTests.cs` | 12 | Clock steps/pause, seeded RNG, construction lifecycle, power idling, exact cost, tech gate, science-point purchases, deterministic replay, 5-neighbour pentagon |
| `SiteScoringTests.cs` | 6 | Composite factors, viability threshold, weight presets, resource deposit, config defaults |
| `TechCatalogueTests.cs` | 3 | Tech ids/prerequisites/unlocks, coverage of every building tech requirement, deterministic display order |
| `WorldLoaderTests.cs` | 18 | Data-root resolution, manifest + tile-header validation, synthetic bake (343 + 286), real Moon and Mars bakes, sampled Mars empty-hex elevation |

## 3. Run the game

```powershell
& $godot --path Game
```

**User arguments go after `--`.** Anything before it is parsed by Godot itself.

```powershell
& $godot --path Game -- --region=589971619905011711 --view=management
```

The runtime needs `data/<body>/manifest.json` and `data/<body>/fine.bin` for the
selected body. Data root resolution order (`BodyWorld.FindDataRoot`):

1. explicit path passed to `BodyWorld.Open`,
2. `SPM_DATA` environment variable (must contain `<body>/manifest.json`),
3. walk up from `AppContext.BaseDirectory` and the current directory.

`--body=moon|mars` selects the body; unknown keys fall back to Moon with a
warning. `M` opens the schematic Solar System Map, where only baked bodies are
travellable. Per-body last-slot settings keep Continue on the same body.

Saves default to the slot root `user://saves/`, which on this machine resolves to
`C:\Users\xenot\AppData\Roaming\Godot\app_userdata\Space Program Manager\saves`.
Each campaign is a child directory (`slot1`, or `slot-<timestamp>` for campaigns
started from the menu). A launch without arguments opens the main menu; arguments
select a slot and start directly. The startup log prints the resolved directory
(`campaign: loaded …`).

Controls are listed in the [README](../README.md#controls).

## 4. Automation

The runtime is built for headless-ish capture: it accepts a full starting state on
the command line, renders a few frames, writes a PNG, and quits.

```powershell
$shot = 'C:\Users\xenot\AppData\Local\Temp\opencode\spm\colony.png'
& $godot --path Game -- --region=589971619905011711 --view=management `
    --overlay=terrain --rot=120 --relief=1.4 --shot=$shot

# Fine hex 0 as a site: two installations, finish construction, capture.
$siteShot = 'C:\Users\xenot\AppData\Local\Temp\opencode\spm\site.png'
& $godot --path Game -- --region=589971619905011711 --view=site --hex=0 `
    --active=air_reclaimer --place=0,0 --place=1,0 --advance=24 --shot=$siteShot
```

| Flag | Meaning |
|---|---|
| `--body=<moon\|mars>` | Select the baked body (default `moon`). |
| `--region=<h3>` | Open the area around a region by H3 index. Without it the last active region is restored. |
| `--view=globe\|region\|management\|site\|area` | Select the active view; `--iso` aliases `management`. |
| `--hex=<ordinal>` | Fine-hex ordinal whose site opens with `--view=site` (default `0`). |
| `--overlay=biome\|terrain\|score\|buildings\|color` | Overlay for area/region/colony views; area ramps are normalised area-wide across all 2,401 hexes. `color`/`colour`/`imagery`/`surface` also drive the globe surface/biome toggle. |
| `--slope` | Toggle the area terrain overlay (terrain ↔ biome); `Tab` cycles all five area overlays. |
| `--projection=flat\|iso` | Select the area projection (`iso` or `isometric`); `G` toggles it at runtime. |
| `--rot=<deg>` | Colony or isometric-area camera angle, quantised to the nearest 60°. |
| `--relief=<scale>` | Colony or isometric-area relief exaggeration (default `1.0`). |
| `--active=<defId>` | Building definition to place. Requires `--view=site`. |
| `--place=<q>,<r>` | Place at a local grid cell. Repeat the flag for more installations; requires `--view=site`. |
| `--route=<from>,<to>` | Plan a rover route between two fine-hex ordinals in the region view. |
| `--dispatch=<rover-id>,<region>,<hex>` | Send a rover along a planned cross-region route. |
| `--speed=<km/h>` | Rover base speed (default `8`). |
| `--yaw=<deg>` `--pitch=<deg>` `--zoom=<factor>` | Initial globe camera. |
| `--slot=<id>` | Open a slot under the save root directly (default: last played, then `slot1`). |
| `--continue` | Open the last-played slot directly. |
| `--menu` | Force the main menu even with other arguments (useful with `--shot`). |
| `--map` | Open the Solar System Map after startup. |
| `--tech` | Open the tech tree after startup. |
| `--fleet` | Open the rover fleet/garage screen after startup. |
| `--save=<dir>` | Override the slot **directory** (default `user://saves/<slotId>`). |
| `--fresh` | Clear the selected slot's region shards and start a new campaign. |
| `--nosave` | Disable persistence for this run. |
| `--time=<0\|1\|2\|4>` | Starting clock speed (default `1×`; `0` pauses). |
| `--advance=<hours>` | Tick loaded regions immediately, save, then continue (e.g. finish a 24 h build). |
| `--shot=<png>` | Wait a few frames, save a screenshot, quit. |
| `--frame-budget=<ms>` | Run the p95 frame-time gate; exits 1 on failure. |
| `--frames=<n>` | Measured frames for the gate, clamped to 30–3600 (default 180). |
| `--fps` | Print FPS plus per-view draw timings. |
| `--redraw` | Force a redraw every frame (benchmarking). |
| `--spin` | Spin the globe. |

Notes:

- `--shot` already waits ~4 frames before capturing; no external sleep is needed.
- Any user argument other than `--menu` skips the main menu, so all pre-M2
  automation keeps working unchanged. `--menu --shot=<png>` captures the front end.
- `--view` overrides the mode restored from `slot.json`, so automation can switch
  away from a saved site view with `--view=region` or `--view=management`.
- `--place`, `--active` and `--hex` only apply with `--view=site`. Region and
  colony views are inspectors; placement there is refused with a warning.
- `--place` takes a local axial cell (`q,r`), not a fine-hex ordinal. Repeating
  `--place` places several installations in the same site in command order.
- `--rot` applies **after** `Load`, so the log prints `camera 0°` first and the
  effective `camera: <n>°` afterwards. That is expected.
- `M` toggles the Solar System Map from any view; `A` opens the area view and `R`
  the fleet screen from any gameplay view. Globe clicks and `--region` open the
  area view by default; clicking a region there drills into the region view.
  `G` toggles the area between its flat tangent-plane map and a seven-region
  isometric diorama; colony view is entered from region view.
- `Tab` toggles surface imagery/biome on the globe and cycles overlays in area,
  region and colony views; `1`–`5` select an overlay directly in those views.
  In isometric area view `Q`/`E` rotate the slab and `[`/`]` adjust relief.
  With a valid sidecar, true colour is the default for every slot unless
  `--overlay` is passed.
- Screenshot and save scratch space for this repo:
  `C:\Users\xenot\AppData\Local\Temp\opencode\spm`.
- `F12` writes `user://moon_overview.png`.

### Performance checks

```powershell
# Exploratory timing: frame time plus per-view draw timings.
& $godot --path Game -- --region=589971619905011711 --view=management --fps --redraw

# Automated gate: 12 warmup + 180 measured frames by default.
& $godot --path Game -- --view=globe --frame-budget=20 --frames=180
```

`--fps` prints frame time and `LastDrawMs` / `AverageDrawMs` for the active view.
`--frame-budget=<ms>` forces redraws, measures p95 frame time and exits 0 on PASS
or 1 on FAIL. The current **20.00 ms** limit came from three Moon globe runs with
p95 `16.82 / 16.67 / 16.67 ms` (about 19% headroom). CI uses `--frames=30` across
21 smokes: colour and biome for globe/region, colour for colony on both bodies,
plus both globes, the unchanged Moon site view, the tech tree screen, the area view
on both bodies, the fleet screen, the area terrain-slope toggle and five isometric
area-overlay runs.

## 5. Data pipeline

First-time setup:

```powershell
py -3.13 -m venv pipeline/.venv
pipeline/.venv/Scripts/python -m pip install -r pipeline/requirements.txt
```

Bake:

```powershell
# fast smoke bake (16 ppd)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 16

# full-fidelity bake (64 ppd, ~41k regions)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 64 --jobs 4

# Mars uses MOLA + USGS geology + Robbins & Hynek craters at the same H3 resolutions
pipeline/.venv/Scripts/python pipeline/bake_mars.py --dem 16
pipeline/.venv/Scripts/python pipeline/bake_mars.py --dem 16 --jobs 4

# deterministic no-download fixtures for CI / local smoke tests
pipeline/.venv/Scripts/python pipeline/bake_moon.py --synthetic --out _ci/moon
pipeline/.venv/Scripts/python pipeline/bake_mars.py --synthetic --out _ci/mars
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=moon --synthetic --out _ci/moon
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=mars --synthetic --out _ci/mars

# optional real colour sidecars after the tile bake exists
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=moon --ppd=16
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=mars --ppd=16

# optional equirectangular previews
pipeline/.venv/Scripts/python pipeline/preview_moon.py
```

| Flag | Default | Meaning |
|---|---|---|
| `--body` | `moon` | Body key; must match `pipeline/spm/bodies.py`. |
| `--dem` | `16` | LOLA DEM resolution: `4`, `16`, `64` pixels/degree. |
| `--fine-res` | per body | Override fine H3 resolution (Moon: 6). |
| `--region-res` | per body | Override region H3 resolution (Moon: 3). |
| `--rim-rings` | `0` | Half-width of the multi-cell crater rim band, in rings. |
| `--jobs` | `1` | Parallel workers for pixel → hex. |
| `--block-rows` | `512` | Raster rows per aggregation block. |
| `--skip-craters` | off | Skip the crater overlay. |
| `--synthetic` | off | Generate deterministic in-memory terrain instead of downloading sources; full tile layout, no real imagery or science fidelity. |
| `--out` | `data/<body>` | Output directory. |
| `--force` | off | Re-download cached sources. |

Output (one tree per body, same layout):

```
data/moon/            # or data/mars/
├─ manifest.json      # 4.2 MB: body, biomes, sources, every region entry
├─ fine.bin           # 54.6 MB: 4-byte record per fine hex, region tiles concatenated
├─ color.bin          # optional 42.4 MB: 3 RGB bytes per fine hex, same child order
├─ imagery.json       # optional provenance + validation metadata
└─ preview_*.png      # optional equirectangular previews
```

`bake_imagery.py` accepts `--body`, `--out`, `--synthetic`, `--force`, `--ppd`
(`4`, `16` or `64`, default `16`), `--jobs` and `--block-rows`. It reads the
existing manifest, refuses a body/resolution/region-count mismatch and never
writes `manifest.json` or `fine.bin`. The runtime validates the body key,
resolutions, hex count, file length and `colorSha256`; any failure logs a warning
and falls back to biome colours.

Sources are cached per body under `pipeline/.cache/<body>/`: Moon uses LDEM
16/64, USGS geology and the Robbins 2019 CSV + zip; Mars uses MOLA MEGDR 16 ppd,
the USGS 15M geologic map and the Robbins & Hynek 2012 catalogue. Colour sidecars
use NASA SVS CGI Moon Kit `lroc_color_poles_2k.tif` and USGS Viking MDIM 2.1
`mars_viking_mdim21_clrmosaic_1km.jpg`; exact URLs, SHA-256 pins and attribution
are in `pipeline/spm/imagery.py` and the generated `imagery.json`. Both `data/`
and `pipeline/.cache/` are gitignored. The Moon bake is deliberately unchanged
(`generatedUtc=2026-11-11T21:48:07+00:00`) so old Moon saves stay valid; the
current Mars bake was regenerated after the empty-hex elevation fix
(`generatedUtc=2026-11-18T19:35:34+00:00`), so the old Mars test slot is
intentionally incompatible.

The pipeline's H3 (`h3==4.5.0`) and the runtime's (`pocketken.H3 4.5.0.1`) must
stay on the same H3 major version — the child-order test guards the tile layout,
not the library version. Bump both together.

## 6. Continuous integration

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests. It sets
`SPM_DATA` to a temporary root and `SPM_REQUIRE_DATA=1`, bakes synthetic Moon and
Mars tile fixtures plus imagery sidecars, runs the Python imagery unit tests,
builds and tests .NET, then downloads the pinned Godot 4.7.2 mono Linux archive
(SHA-256 `129f82db7bafd54ae14bb5bb284041c73860e8c7a009a3a026ca5e946cbff247`).
The Godot step runs twenty-one `--frame-budget=20 --frames=30` smokes: colour and
biome coverage for globe/region, colour for colony on both bodies, the unchanged
Moon site view, the tech screen, the area view on both bodies, the fleet screen,
the area terrain-slope toggle and the five isometric area-overlay combinations.
Headless `--shot` is deliberately not used because it
hangs in automation; the frame-gate run is the smoke and exit-code check. No
`data/` is committed.

## 7. Repository conventions

- **LF line endings.** Git may warn that LF will become CRLF; ignore the warning.
  **Do not** rewrite files with `Get-Content -Raw | Set-Content` — it has caused
  encoding damage (mojibake in comments) in this repo before. Use an editor or the
  edit tool.
- C# `net8.0`, nullable enabled, implicit usings, file-scoped namespaces.
- Sim namespaces mirror folders: `Sim.World`, `Sim.Campaign`, `Sim.Buildings`, …
- Godot `.uid` files are **tracked**; commit them with new scripts.
- Tests: xUnit, one file per subsystem, descriptive `Subject_ExpectedBehaviour` names.
- Commits: imperative subject, body explaining why and how it was verified.

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Could not find '<body>/manifest.json'` | No bake for the selected body, or wrong working directory | Run the matching bake, or set `SPM_DATA` to the folder containing `moon/` and `mars/` |
| `Manifest ... describes body ...` | `--body` / directory mismatch | Match the folder name to the manifest's `body.key` |
| `Tile file for body ... not found` | `fine.bin` missing but manifest present | Re-run the bake |
| `surface imagery unavailable: ... using biome colours` | Missing/short/wrong-body/wrong-resolution/corrupt `color.bin` or `imagery.json` | Re-run `bake_imagery.py` for this body, or ignore: the game remains playable with biome colours |
| Save status `save · disabled · incompatible` | Version, format, or fingerprint mismatch | Keep the save; re-bake with the same inputs or start a new slot with `--fresh` |
| `.corrupt` files appear in `regions/` | Unreadable shard was quarantined | Inspect the JSON; the live region was installed empty |
| `InvalidDataException` on load | Manifest/tile header validation failed | Check `TileFormat.Version` matches `pipeline/spm/config.py` |
| Godot says C# is unavailable | Standard (non-.NET) Godot build | Use the mono/.NET build |
| `--rot` seems ignored | It only applies in the colony view | Use `--view=management` or press `M` first |
| `--place` warns "requires --view=site" | Region/colony views no longer place buildings | Add `--view=site --hex=<ordinal>` and pass `--place=<q>,<r>` |
| Installation vanishes after reload | Local footprint could not be rebuilt (unknown def, invalid parent hex, outside the 37-cell disk, or local overlap) | Check the `campaign:` warnings printed on load |
| Tests fail only on another machine | Real-bake tests assume `data/moon` and `data/mars` | They skip when absent; with `SPM_REQUIRE_DATA=1` they must run, so bake both bodies first |
