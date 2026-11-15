# Space Program Manager

A space-agency management game built in **Godot 4.7 (.NET / C#)**. Real Solar
System bodies are baked offline into an **H3** hexagonal grid; the runtime renders
a globe, a per-region hex map, and a close-up isometric colony view where you
place and remove buildings. Campaign state is engine-free and saved as JSON.

## Status at a glance

Snapshot: **2026-11-15**, commit `b3b09a1` on `main`, **88 tests passing**.

| Area | State |
|---|---|
| Moon data pipeline | **Done** — 41,162 regions, 14,117,882 fine hexes, biome + crater + elevation |
| Sim core (engine-free) | **Done** — H3, biomes, movement, routing, tiles, site scoring, buildings, campaign, save store |
| Godot runtime | **Done** — globe, region view, isometric colony view, placement, save/load, CLI automation |
| Simulation loop | **Not started** — no clock, construction progress, economy, power/life support or tech unlocks |
| Campaign shell | **Next** — menu, save slots, notifications, region search, visited markers |
| Site view (per-hex grid) | **Planned** — see `docs/roadmap.md` |
| Multi-body | **Planned** — Moon only today |

The full milestone plan lives in [`docs/roadmap.md`](docs/roadmap.md) and is
mirrored in the working plan at `~/.opencode/plan/game_development_plan.md`.

## Repository layout

| Path | Purpose |
|---|---|
| `Sim/` | Engine-free simulation core (C# class library). **No Godot references** — deterministic and unit-testable. |
| `Sim.Tests/` | xUnit tests for the simulation core. |
| `Game/` | Godot 4.7.2 project (C#). References `Sim`. |
| `pipeline/` | Python 3.13 offline data pipeline (H3, rasterio, geopandas). |
| `data/` | Generated tiles (gitignored). The game needs `data/moon/`. |
| `docs/` | Architecture, save format, roadmap, development notes, decisions. |

## Requirements

- **Godot 4.7.2** — the **.NET/mono build** (the standard build has no C# support).
- **.NET SDK 8+** — projects target `net8.0` (matches Godot 4.7).
- **Python 3.13** — pipeline only (`h3` has no Python 3.15 wheel yet).

## Build & test

```sh
dotnet build SpaceProgramManager.slnx
dotnet test Sim.Tests/Sim.Tests.csproj
```

## Run the game

Open `Game/project.godot` in the Godot .NET editor, or from the command line:

```sh
godot --path Game
```

The game expects `data/moon/manifest.json` + `data/moon/fine.bin`. It searches
upward from the executable and honours the `SPM_DATA` environment variable
(pointing at the folder that contains `moon/`).

### Controls

**Globe**

| Input | Action |
|---|---|
| Left drag | Rotate the Moon |
| Wheel | Zoom |
| Right drag | Pan |
| Left click | Open the region under the cursor |
| F12 | Screenshot to `user://moon_overview.png` |
| Esc | Quit |

**Region view**

| Input | Action |
|---|---|
| Wheel / right drag | Zoom / pan |
| Left click | Select hex; a second click plans a rover route |
| `1` `2` `3` `4` / Tab | Biome / terrain / site score / buildings overlay |
| `M` | Enter the colony view |
| `,` `.` | Cycle the active building definition |
| Left click (Buildings overlay) | Place the active building |
| Del / Backspace (Buildings overlay) | Remove the hovered building |
| `C` | Clear all buildings in the region |
| Esc | Back to the globe |

**Colony view (isometric)**

| Input | Action |
|---|---|
| Wheel / right drag | Zoom / pan |
| Left click | Select hex |
| `Q` / `E` | Orbit the camera by 60° |
| `[` / `]` | Decrease / increase relief exaggeration |
| `1`–`4` / Tab | Overlay |
| `C` | Place the active building at the selected hex |
| Del / Backspace | Remove the building at the selected hex |
| `,` `.` | Cycle the active building definition |
| Esc | Back to the region view |

The bottom-right HUD shows the current save status. Saving is immediate on every
placement or removal, and metadata is flushed on exit.

## Command-line automation

Everything after `--` is read as a user argument. Typical headless capture:

```sh
godot --path Game -- --region=589971619905011711 --view=management \
  --overlay=terrain --rot=120 --shot=C:/tmp/colony.png
```

| Flag | Meaning |
|---|---|
| `--region=<h3>` | Open a region by H3 index (restores the last active region when omitted) |
| `--view=management` or `--iso` | Enter the colony view after loading the region |
| `--overlay=biome\|terrain\|score\|buildings` | Select the overlay for the active mode |
| `--rot=<degrees>` | Colony camera angle, quantised to the nearest 60° |
| `--relief=<scale>` | Colony relief exaggeration (default `1.0`) |
| `--active=<defId>` | Select the building definition to place |
| `--place=<ordinal>[,<ordinal>…]` | Place the active building at region hex ordinals |
| `--route=<from>,<to>` | Plan a rover route between two hex ordinals |
| `--speed=<km/h>` | Rover base speed (default `10`) |
| `--yaw=<deg>` `--pitch=<deg>` `--zoom=<factor>` | Initial globe camera |
| `--save=<dir>` | Save slot directory (default `user://saves/slot1`) |
| `--fresh` | Clear the slot's region shards and start a new campaign |
| `--nosave` | Disable persistence for this run |
| `--shot=<png>` | Render a few frames, save a screenshot, quit |
| `--fps` / `--redraw` / `--spin` | Print FPS + draw timings / force redraws / spin the globe |

## Saving

Campaigns live in a slot directory:

```
<slot>/
├─ campaign.json              # clock, treasury, stockpile, visited/active regions, bake fingerprint
└─ regions/
   └─ <h3-index>.json         # buildings for one region
```

Writes are atomic and keep a `.bak`; unreadable shards are quarantined as
`.corrupt` instead of being overwritten. See [`docs/save-format.md`](docs/save-format.md).

## Data pipeline

```sh
py -3.13 -m venv pipeline/.venv
pipeline/.venv/Scripts/python -m pip install -r pipeline/requirements.txt

# fast smoke bake (16 ppd)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 16

# full-fidelity bake (64 ppd, ~41k regions)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 64 --jobs 4

# optional equirectangular previews
pipeline/.venv/Scripts/python pipeline/preview_moon.py
```

Sources: LOLA LDEM (NASA PDS), USGS Unified Geologic Map of the Moon 2020,
Robbins (2019) lunar crater catalogue. See [`docs/development.md`](docs/development.md).

## Design notes

- **Fine hex** — H3 resolution 6 on the Moon (~0.9 km edge): the buildable cell.
- **Region** — H3 resolution 3 (~60 km): the data tile and overview unit. Each
  region contains **343** fine hexes (286 on the 12 pentagons).
- Biomes are assigned by area share, then neighbour majority, then distance,
  then a deterministic priority list.
- Movement uses a vessel-speed model: uphill ×0.8, downhill ×1.15, crater rims
  apply a non-stacking penalty (single-cell 30%, multi-cell 20%).
- Craters are a movement overlay, not a biome.

## Documentation

| Document | Contents |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | Layers, data flow, coordinate systems, rendering, extension points |
| [`docs/save-format.md`](docs/save-format.md) | Save schema, invariants, atomicity, compatibility policy |
| [`docs/roadmap.md`](docs/roadmap.md) | Milestones (M2 first, then M1, M3–M6), acceptance criteria, open questions |
| [`docs/development.md`](docs/development.md) | Build/test, automation, pipeline, conventions, troubleshooting |
| [`docs/decisions.md`](docs/decisions.md) | Decision log (ADR-style) with rationale and commit references |
| [`AGENTS.md`](AGENTS.md) | Repository rules for AI coding sessions |
