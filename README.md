# Space Program Manager

A space-agency management game built in **Godot 4.7 (.NET / C#)**.

## Repository layout

| Path | Purpose |
|---|---|
| `Sim/` | Engine-free simulation core (C# class library). **No Godot references** — deterministic and unit-testable. |
| `Sim.Tests/` | xUnit tests for the simulation core. |
| `Game/` | Godot 4.7.2 project (C#). References `Sim`. |
| `pipeline/` | Python 3.13 offline data pipeline (H3, rasterio, geopandas). |

## Requirements

- **Godot 4.7.2** — the **.NET/mono build** (the standard build has no C# support).
- **.NET SDK 8+** — projects target `net8.0` (matches Godot 4.7).
- **Python 3.13** — pipeline only. (`h3` has no Python 3.15 wheel yet.)

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

## Data pipeline

```sh
py -3.13 -m venv pipeline/.venv
pipeline/.venv/Scripts/python -m pip install -r pipeline/requirements.txt
```

## Design notes

The hex-grid / biome system is built on **H3** (Uber's hierarchical hexagonal index):

- **Fine hex** — H3 resolution 7 (~1.2 km edge, ~5.2 km²): the buildable gameplay cell.
- **Region** — H3 resolution 3 (~60 km): the coarse parent cell used as a data tile / overview unit.

Biome assignment per hex: dominant area share → neighbour majority → distance-based → fixed priority.
Movement uses a vessel-speed model: uphill ×0.8, downhill ×1.15, with crater overlays that do not stack.
