# Space Program Manager

A space-agency management game built in **Godot 4.7 (.NET / C#)**. Real Solar
System bodies are baked offline into an **H3** hexagonal grid; the runtime renders
a globe, a per-region hex map, and a close-up isometric colony view where you
place and remove buildings. Campaign state is engine-free and saved as JSON.

## Status at a glance

Snapshot: **2026-11-17**, M1 simulation loop, **113 tests passing**.

| Area | State |
|---|---|
| Moon data pipeline | **Done** — 41,162 regions, 14,117,882 fine hexes, biome + crater + elevation |
| Sim core (engine-free) | **Done** — H3, biomes, movement, routing, tiles, site scoring, buildings, campaign, save store, slot catalogue |
| Godot runtime | **Done** — main menu, slots, globe, region view, isometric colony view, placement, save/load, CLI automation |
| Campaign shell | **Done (M2)** — new/continue/load, manual save, toasts, pause scaffold, region search, settings |
| Simulation loop | **Done (M1)** — deterministic clock, construction, economy, power/life support, tech enforcement, save v2 |
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

A launch without arguments opens the main menu. The menu and pause screens use
`↑` `↓` (or `W` `S`) to select, `Enter` to choose, `Esc` to go back, and the
mouse for the same actions.

**Menus**

| Input | Action |
|---|---|
| Continue | Open the last-played slot |
| New campaign | Create an auto-named slot and start immediately |
| Load campaign | Pick a slot; `×` deletes after confirmation |
| Settings | Save root, keybind reference, open save folder |
| Pause (`Esc` in game) | Resume, time speed (Pause / `1` / `2` / `4`), save now (`F5`), load, settings, main menu, quit |
| `/` on the globe | Search a region or fine-hex H3 index |

**Globe**

| Input | Action |
|---|---|
| Left drag | Rotate the Moon |
| Wheel | Zoom |
| Right drag | Pan |
| Left click | Open the region under the cursor |
| `/` | Jump to an H3 index |
| `Home` | Focus the active region |
| `Space` | Pause / resume time (resumes at 1×) |
| `F5` | Save now |
| F12 | Screenshot to `user://moon_overview.png` |
| Esc | Pause menu |

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

A session starts at 1×. `Space` pauses/resumes the clock in every view; the pause
menu also has direct Pause / 1× / 2× / 4× buttons. At 1× one real second is one
game hour; steps are 0.25 h. The bottom-left HUD shows the day/hour, speed and credits, plus power
supply/demand, idle count and the first status reason when a region is open.
The bottom-right HUD shows the current save status. Saving is immediate on every
placement or removal, on pause, every 5 seconds, and on exit.

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
| `--slot=<id>` | Open a slot under the save root directly |
| `--continue` | Open the last-played slot directly |
| `--menu` | Force the main menu (pair with `--shot` to capture it) |
| `--save=<dir>` | Override the slot **directory** (default `user://saves/<slotId>`) |
| `--fresh` | Clear the selected slot's region shards and start a new campaign |
| `--nosave` | Disable persistence for this run |
| `--time=<0\|1\|2\|4>` | Starting speed (default `1×`; `0` pauses) |
| `--advance=<hours>` | Tick loaded regions immediately, save, then continue |
| `--shot=<png>` | Render a few frames, save a screenshot, quit |
| `--fps` / `--redraw` / `--spin` | Print FPS + draw timings / force redraws / spin the globe |

Any user argument except `--menu` skips the menu and starts the selected slot, so
pre-M2 automation is unchanged.

## Saving

Campaigns live under `user://saves/<slotId>/`:

```
<slot>/
├─ campaign.json              # clock, treasury, stockpile, visited/active regions, bake fingerprint
├─ slot.json                  # derived list summary + last view (safe to delete)
└─ regions/
   └─ <h3-index>.json         # buildings for one region
```

Writes are atomic and keep a `.bak`; unreadable shards are quarantined as
`.corrupt` instead of being overwritten. The campaign schema is **v2**; v1 saves
are migrated in memory with starting credits, stockpile and a deterministic RNG
seed. Saving happens on every placement or removal, on pause, every 5 seconds,
on `F5`, and on exit. See [`docs/save-format.md`](docs/save-format.md).

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
