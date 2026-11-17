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

Expected: `powodzenie: 132` — 132 passed, 0 failed, 0 skipped on a machine with
`data/moon` present. Tests that need the real bake **skip** when it is absent — do
not make them fail. Set `SPM_REQUIRE_DATA=1` to turn that skip into a failure
(intended for CI).

`dotnet build` builds `Sim`, `Sim.Tests`, and `Game`. Building `Game` requires the
Godot .NET SDK (`Godot.NET.Sdk/4.7.2`) to be resolvable, which the mono editor
installs.

### Test inventory

| File | Tests | Covers |
|---|---:|---|
| `BuildingTests.cs` | 15 | Registry consistency, placement rejection paths, footprint shape, tech filtering, real-bake biome ids |
| `CampaignStoreTests.cs` | 21 | Meta/shard round-trip, v1 meta + v1/v2 → v3 shard migration, v3 two-installation sites, fingerprint + version + format rejection, backups, corrupt quarantine, unknown ids, state value filtering, site view state |
| `CampaignSlotTests.cs` | 15 | Slot discovery/ordering, `slot.json` creation + rebuild, fingerprint/unreadable states, delete safety, view-state round-trip |
| `CampaignTests.cs` | 16 | Site-scoped place/remove/clear, local preview footprint, two installations in one hex, independent sites, local overlap/clipping, install-from-save validation, visited tracking, change events |
| `ChildOrderTests.cs` | 2 | H3 child enumeration order matches h3-py (343 and 286 cells) |
| `DominantBiomeResolverTests.cs` | 7 | Area share, neighbour majority, distance fallback, priority order |
| `HexGridTests.cs` | 5 | H3 index ↔ lat/long round-trip, 6 neighbours on a normal cell, region parent/children, canonical H3 example |
| `LocalGridTests.cs` | 7 | 37-cell disk, centre-first order, boundary rejection, axial distance, local footprint sizes and ring-3 clipping |
| `MovementModelTests.cs` | 6 | Uphill/downhill multipliers, crater penalties, non-stacking rule |
| `RoutePlannerTests.cs` | 6 | Cheapest-time path, crater on destination, unreachable goal, empty route, leg totals |
| `SimulationTests.cs` | 8 | Clock steps/pause, seeded RNG, construction lifecycle, power idling, exact cost, tech gate, deterministic replay, 5-neighbour pentagon |
| `SiteScoringTests.cs` | 6 | Composite factors, viability threshold, weight presets, resource deposit, config defaults |
| `WorldLoaderTests.cs` | 15 | Data-root resolution, manifest + tile-header validation, synthetic bake (343 + 286), real Moon bake |

## 3. Run the game

```powershell
& $godot --path Game
```

**User arguments go after `--`.** Anything before it is parsed by Godot itself.

```powershell
& $godot --path Game -- --region=589971619905011711 --view=management
```

The runtime needs `data/moon/manifest.json` and `data/moon/fine.bin`. Data root
resolution order (`BodyWorld.FindDataRoot`):

1. explicit path passed to `BodyWorld.Open`,
2. `SPM_DATA` environment variable (must contain `moon/manifest.json`),
3. walk up from `AppContext.BaseDirectory` and the current directory.

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
| `--region=<h3>` | Open a region by H3 index. Without it the last active region is restored. |
| `--view=region\|management\|site` | Select the active view; `--iso` aliases `management`. |
| `--hex=<ordinal>` | Fine-hex ordinal whose site opens with `--view=site` (default `0`). |
| `--overlay=biome\|terrain\|score\|buildings\|sites` | Overlay for region/colony views. |
| `--rot=<deg>` | Colony camera angle, quantised to the nearest 60°. |
| `--relief=<scale>` | Colony relief exaggeration (default `1.0`). |
| `--active=<defId>` | Building definition to place. Requires `--view=site`. |
| `--place=<q>,<r>` | Place at a local grid cell. Repeat the flag for more installations; requires `--view=site`. |
| `--route=<from>,<to>` | Plan a rover route between two fine-hex ordinals. |
| `--speed=<km/h>` | Rover base speed (default `10`). |
| `--yaw=<deg>` `--pitch=<deg>` `--zoom=<factor>` | Initial globe camera. |
| `--slot=<id>` | Open a slot under the save root directly (default: last played, then `slot1`). |
| `--continue` | Open the last-played slot directly. |
| `--menu` | Force the main menu even with other arguments (useful with `--shot`). |
| `--save=<dir>` | Override the slot **directory** (default `user://saves/<slotId>`). |
| `--fresh` | Clear the selected slot's region shards and start a new campaign. |
| `--nosave` | Disable persistence for this run. |
| `--time=<0\|1\|2\|4>` | Starting clock speed (default `1×`; `0` pauses). |
| `--advance=<hours>` | Tick loaded regions immediately, save, then continue (e.g. finish a 24 h build). |
| `--shot=<png>` | Wait a few frames, save a screenshot, quit. |
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
- Screenshot and save scratch space for this repo:
  `C:\Users\xenot\AppData\Local\Temp\opencode\spm`.
- `F12` writes `user://moon_overview.png`.

### Performance checks

```powershell
& $godot --path Game -- --region=589971619905011711 --view=management --fps --redraw
```

`--fps` prints frame time and `LastDrawMs` / `AverageDrawMs` for the active view.
Use it before and after renderer changes; there is no automated budget yet (see
the roadmap's cross-cutting backlog).

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
| `--out` | `data/<body>` | Output directory. |
| `--force` | off | Re-download cached sources. |

Output:

```
data/moon/
├─ manifest.json      # 4.2 MB: body, biomes, sources, every region entry
├─ fine.bin           # 54.6 MB: 4-byte record per fine hex, region tiles concatenated
└─ preview_*.png      # optional equirectangular previews
```

Sources are cached under `pipeline/.cache/moon/` (LDEM 16/64, USGS geology,
Robbins 2019 CSV + zip). Both `data/` and `pipeline/.cache/` are gitignored.

The pipeline's H3 (`h3==4.5.0`) and the runtime's (`pocketken.H3 4.5.0.1`) must
stay on the same H3 major version — the child-order test guards the tile layout,
not the library version. Bump both together.

## 6. Repository conventions

- **LF line endings.** Git may warn that LF will become CRLF; ignore the warning.
  **Do not** rewrite files with `Get-Content -Raw | Set-Content` — it has caused
  encoding damage (mojibake in comments) in this repo before. Use an editor or the
  edit tool.
- C# `net8.0`, nullable enabled, implicit usings, file-scoped namespaces.
- Sim namespaces mirror folders: `Sim.World`, `Sim.Campaign`, `Sim.Buildings`, …
- Godot `.uid` files are **tracked**; commit them with new scripts.
- Tests: xUnit, one file per subsystem, descriptive `Subject_ExpectedBehaviour` names.
- Commits: imperative subject, body explaining why and how it was verified.

## 7. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Could not find 'moon/manifest.json'` | No bake, or wrong working directory | Run the bake, or set `SPM_DATA` to the folder containing `moon/` |
| `Manifest ... describes body ...` | `--body` / directory mismatch | Match the folder name to the manifest's `body.key` |
| `Tile file for body ... not found` | `fine.bin` missing but manifest present | Re-run the bake |
| Save status `save · disabled · incompatible` | Version, format, or fingerprint mismatch | Keep the save; re-bake with the same inputs or start a new slot with `--fresh` |
| `.corrupt` files appear in `regions/` | Unreadable shard was quarantined | Inspect the JSON; the live region was installed empty |
| `InvalidDataException` on load | Manifest/tile header validation failed | Check `TileFormat.Version` matches `pipeline/spm/config.py` |
| Godot says C# is unavailable | Standard (non-.NET) Godot build | Use the mono/.NET build |
| `--rot` seems ignored | It only applies in the colony view | Use `--view=management` or press `M` first |
| `--place` warns "requires --view=site" | Region/colony views no longer place buildings | Add `--view=site --hex=<ordinal>` and pass `--place=<q>,<r>` |
| Installation vanishes after reload | Local footprint could not be rebuilt (unknown def, invalid parent hex, outside the 37-cell disk, or local overlap) | Check the `campaign:` warnings printed on load |
| Tests fail only on another machine | Real-bake tests assume `data/moon` | They skip when it is absent; if they fail, the bake is present but inconsistent |
