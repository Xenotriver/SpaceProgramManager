# Space Program Manager

A space-agency management game built in **Godot 4.7 (.NET / C#)**. Real Solar
System bodies are baked offline into an **H3** hexagonal grid; the runtime renders
a globe, a multi-region **area view** for situational awareness and rover logistics,
a per-region hex map, a region-scale isometric colony view, and a close-up per-hex
**site view** where installations are placed on a local grid. Campaign state is
engine-free and saved as JSON. Moon and Mars are playable.

## Status at a glance

Snapshot: **2026-11-19**, M4.5 true-colour overlay, faceted colony terrain, M5 C0 save v4, C1 tech tree and C2 rover logistics, **167 C# tests + 5 pipeline tests passing**.

| Area | State |
|---|---|
| Moon data pipeline | **Done** — 41,162 regions, 14,117,882 fine hexes, biome + crater + elevation |
| Mars data pipeline | **Done (M4)** — 41,162 regions, 7 biomes, MOLA elevation, geology and craters |
| Sim core (engine-free) | **Done** — H3, biomes, movement, routing, tiles, site scoring, per-body building catalogues, sites, campaign, save store, slot catalogue |
| Godot runtime | **Done** — main menu, slots, body selection, system map, globe, region/colony inspectors, site placement, save/load, CLI automation |
| Campaign shell | **Done (M2, extended in M4)** — new/continue/load, manual save, toasts, pause, region search, settings, per-body last slots |
| M5 save contract | **Done (M5 C0)** — schema v4 for contracts, events, rover fleet and typed failure/repair; v1–v3 migrate in memory |
| Tech tree | **Done (M5 C1)** — science-point purchases, prerequisites, `T` screen and `--tech` automation |
| Rover logistics | **Done (M5 C2)** — garage purchases, cross-region routing, fleet screen, `A`/`R` keys, area view; D39 |
| Area-first navigation | **Experimental** — globe click/`--region` opens area view; `G` toggles area-scale faceted slope shading; D40 |
| Simulation loop | **Done (M1)** — deterministic clock, construction, economy, power/life support, tech enforcement |
| Site view (per-hex grid) | **Done (M3)** — 37-cell radius-3 local grid; multiple installations per fine hex |
| Multi-body | **Done (M4)** — Moon + Mars, body-keyed saves, cross-body refusal, system-map travel |
| Surface imagery | **Done (M4.5)** — optional real Moon/Mars colour sidecars in globe, region and colony views; biome fallback |
| CI and performance gate | **Done (M4, extended M4.5/M5)** — synthetic tile + imagery bakes, 15 headless Godot smokes, 20 ms p95 frame budget |

The full milestone plan lives in [`docs/roadmap.md`](docs/roadmap.md) and is
mirrored in the working plan at `~/.opencode/plan/game_development_plan.md`.

## Repository layout

| Path | Purpose |
|---|---|
| `Sim/` | Engine-free simulation core (C# class library). **No Godot references** — deterministic and unit-testable. |
| `Sim.Tests/` | xUnit tests for the simulation core. |
| `Game/` | Godot 4.7.2 project (C#). References `Sim`. |
| `pipeline/` | Python 3.13 offline data pipeline (H3, rasterio, geopandas). |
| `data/` | Generated tiles and optional colour sidecars (gitignored). The game uses `data/moon/` and `data/mars/`; CI generates synthetic fixtures. |
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

The game expects `data/<body>/manifest.json` + `data/<body>/fine.bin` for the
selected body (`moon` by default, or `mars`). Optional `data/<body>/color.bin`
and `imagery.json` add true-colour surface imagery; if either is missing or
invalid the game warns and uses biome colours. Data root resolution searches
upward from the executable and honours the `SPM_DATA` environment variable
(pointing at the folder that contains `moon/` and `mars/`).

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
| `M` | Open/close the schematic Solar System Map (Moon and Mars are ready) |
| `T` | Open/close the tech tree |
| `A` | Open the area view (the default drill-in from the globe) |
| `R` | Open the rover fleet / garage screen |

**Globe**

| Input | Action |
|---|---|
| Left drag | Rotate the active body |
| Wheel | Zoom |
| Right drag | Pan |
| Left click | Open the area around the region under the cursor |
| `Tab` | Toggle surface imagery / biome colours (when a sidecar is available) |
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
| `1`–`5` / Tab | Biome / terrain / site score / sites / surface imagery overlay |
| `Enter` | Open the selected fine hex as a site (hex 0 when none is selected) |
| `G` | Enter the colony view |
| `M` | Open the Solar System Map |
| Esc | Back to the globe |

**Area view (rover logistics and terrain slope)**

| Input | Action |
|---|---|
| Wheel / right drag | Zoom / pan |
| Hover | Outline a region and describe the rover or region under the cursor |
| Left click a rover | Select it as the source of the next dispatch |
| Left click a region | Open that region, or dispatch the selected rover to its centre |
| `G` | Toggle faceted terrain-slope shading across the visible region tiles |
| `R` | Open the fleet screen to buy rovers or choose a rover |
| Esc | Clear the selected rover, then back to the globe |

The experimental navigation order is **globe → area → region → site**: clicking a
region on the globe or starting with `--region` lands in the area view; clicking a
region there drills into the region view. `--view=region`, `--view=management`,
`--view=site` and `--view=globe` still open those views directly.

**Colony view (isometric)**

| Input | Action |
|---|---|
| Wheel / right drag | Zoom / pan |
| Left click | Select hex |
| `Q` / `E` | Orbit the camera by 60° |
| `[` / `]` | Decrease / increase relief exaggeration |
| `1`–`5` / Tab | Overlay (biome / terrain / site score / sites / surface imagery) |
| `Enter` | Open the selected fine hex as a site (hex 0 when none is selected) |
| `M` | Open the Solar System Map |
| Esc | Back to the region view |

**Site view (per-hex grid)**

| Input | Action |
|---|---|
| Wheel / right drag | Zoom / pan |
| Hover | Preview the active building's local footprint |
| Left click | Select a local cell and place the active building there |
| `,` / `.` | Cycle the active building definition |
| Del / Backspace | Remove the installation under the selection (or cursor) |
| `C` | Clear installations in this site only |
| Esc | Back to the region or colony view |

Region and colony views are **inspectors**: they draw one aggregate marker per
occupied fine hex with a count and the most urgent status. All placement and
removal happens in the site view through `CampaignState`.

Rovers are not granted at campaign start. Build and finish a **Rover Garage**,
then buy Rover Mk I units from the `R` fleet screen. Select an available rover and
click a region in the area view to dispatch it; travel completes on the simulation
clock. The cross-region route is derived from H3 neighbours and movement costs and
is never stored in the save. Cargo is persisted and shown in the fleet screen;
transfer rules belong to a later content step.

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

# Site: two installations in one fine hex, then advance construction.
godot --path Game -- --region=589971619905011711 --view=site --hex=0 \
  --active=air_reclaimer --place=0,0 --place=1,0 --advance=24 \
  --shot=C:/tmp/site.png
```

| Flag | Meaning |
|---|---|
| `--body=<moon\|mars>` | Select the baked body to load (default `moon`) |
| `--region=<h3>` | Open the area around a region by H3 index (restores the last active region when omitted) |
| `--view=globe\|region\|management\|site\|area` | Select the active view; `--iso` aliases `management` |
| `--hex=<ordinal>` | Fine-hex ordinal whose site opens with `--view=site` (default `0`) |
| `--overlay=biome\|terrain\|score\|buildings\|color` | Select the overlay; `color`, `colour`, `imagery` and `surface` are aliases, globe accepts `color`/`biome`, and area accepts `terrain` for slope shading |
| `--slope` | Toggle area-scale terrain-slope shading (same action as `G` in the area view) |
| `--rot=<degrees>` | Colony camera angle, quantised to the nearest 60° |
| `--relief=<scale>` | Colony relief exaggeration (default `1.0`) |
| `--active=<defId>` | Select the building definition to place; site view only |
| `--place=<q>,<r>` | Place the active building at a local grid cell; repeat the flag for more installations; requires `--view=site` |
| `--route=<from>,<to>` | Plan a rover route between two fine-hex ordinals in the region view |
| `--dispatch=<rover-id>,<region>,<hex>` | Dispatch a rover along a planned cross-region route |
| `--speed=<km/h>` | Rover base speed (default `8`) |
| `--yaw=<deg>` `--pitch=<deg>` `--zoom=<factor>` | Initial globe camera |
| `--slot=<id>` | Open a slot under the save root directly |
| `--continue` | Open the last-played slot directly |
| `--menu` | Force the main menu (pair with `--shot` to capture it) |
| `--map` | Open the Solar System Map after startup |
| `--tech` | Open the tech tree after startup |
| `--fleet` | Open the rover fleet/garage screen after startup |
| `--save=<dir>` | Override the slot **directory** (default `user://saves/<slotId>`) |
| `--fresh` | Clear the selected slot's region shards and start a new campaign |
| `--nosave` | Disable persistence for this run |
| `--time=<0\|1\|2\|4>` | Starting speed (default `1×`; `0` pauses) |
| `--advance=<hours>` | Tick loaded regions immediately, save, then continue |
| `--shot=<png>` | Render a few frames, save a screenshot, quit |
| `--frame-budget=<ms>` | Run the p95 frame-time gate (12 warmup frames, exit 1 on failure) |
| `--frames=<n>` | Measured frames for the gate, clamped to 30–3600 (default 180) |
| `--fps` / `--redraw` / `--spin` | Print FPS + draw timings / force redraws / spin the globe |

Any user argument except `--menu` skips the menu and starts the selected slot, so
pre-M2 automation is unchanged. With a valid imagery sidecar, true colour is the
default in globe/region/colony views; `--overlay=biome` or `Tab` selects the
analytical view. Without imagery, the previous per-view defaults remain. Cross-body
slots are listed as incompatible and refused; they are never overwritten.

## Saving

Campaigns live under `user://saves/<slotId>/`:

```
<slot>/
├─ campaign.json              # clock, treasury, stockpile, visited/active regions, bake fingerprint
├─ slot.json                  # derived list summary + last view (safe to delete)
└─ regions/
   └─ <h3-index>.json         # sites and installations for one region
```

Writes are atomic and keep a `.bak`; unreadable shards are quarantined as
`.corrupt` instead of being overwritten. The campaign schema is **v4**; v1–v3
saves are migrated in memory and upgraded only on the next save. v4 adds
contracts, events, rover fleet state and typed failure/repair records. Saving
happens on every placement or removal, on pause, every 5 seconds, on `F5`, and
on exit.
See [`docs/save-format.md`](docs/save-format.md).

## Data pipeline

```sh
py -3.13 -m venv pipeline/.venv
pipeline/.venv/Scripts/python -m pip install -r pipeline/requirements.txt

# fast smoke bake (16 ppd)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 16

# full-fidelity bake (64 ppd, ~41k regions)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --dem 64 --jobs 4

# Mars uses the same resolutions with MOLA / USGS geology / Robbins craters
pipeline/.venv/Scripts/python pipeline/bake_mars.py --dem 16

# deterministic no-download fixtures (used by CI)
pipeline/.venv/Scripts/python pipeline/bake_moon.py --synthetic --out _ci/moon
pipeline/.venv/Scripts/python pipeline/bake_mars.py --synthetic --out _ci/mars
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=moon --synthetic --out _ci/moon
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=mars --synthetic --out _ci/mars

# optional real colour sidecars after the tile bake exists
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=moon
pipeline/.venv/Scripts/python pipeline/bake_imagery.py --body=mars

# optional equirectangular previews
pipeline/.venv/Scripts/python pipeline/preview_moon.py
```

Sources: LOLA LDEM, USGS Unified Geologic Map of the Moon 2020 and Robbins (2019)
lunar craters; MOLA MEGDR 16 ppd, USGS Mars 15M Geologic Map and Robbins & Hynek
(2012) Mars craters. Colour sidecars use NASA SVS CGI Moon Kit LROC WAC and the
USGS Viking MDIM 2.1 colorized Mars mosaic; both are public-domain/non-copyright
US government imagery with attribution recorded in `imagery.json`. Sidecars are
presentation-only and never change `manifest.json`, `fine.bin` or a save. See
[`docs/development.md`](docs/development.md).

## Surface imagery

`bake_imagery.py` writes two optional files beside an existing tile bake:

```
data/<body>/
├─ manifest.json   # unchanged by the imagery bake
├─ fine.bin        # unchanged by the imagery bake
├─ color.bin       # 3 bytes per fine hex, in fine.bin child order
└─ imagery.json    # provenance, resolutions, hex count and colour SHA-256
```

The runtime validates the body key, resolutions, hex count, file length and hash.
A missing, short, wrong-body, wrong-resolution or corrupt sidecar prints a warning
and falls back to biome colours without blocking the load. The globe uses one mean
colour per region; region and colony views use per-hex colours. The local site grid
and all placement rules are unchanged.

## Design notes

- **Fine hex** — H3 resolution 6 on the Moon (~0.9 km edge): a site container,
  not a single-building slot.
- **Site** — a radius-3 axial disk of **37** local cells inside one fine hex.
  Installations occupy local cells; footprints are derived from the definition.
- **Region** — H3 resolution 3 (~60 km): the data tile and overview unit. Each
  region contains **343** fine hexes (286 on the 12 pentagons). Mars keeps the
  same resolutions, so saves and tile code are identical; only ground area and
  body-specific data differ.
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
