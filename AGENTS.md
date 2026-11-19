# AGENTS.md — repository rules

Instructions for AI coding sessions working in this repository. Read this file
first, then the document relevant to your task.

## What this project is

A space-agency management game: a Python pipeline bakes real Solar System data
into H3 tiles, an engine-free C# simulation core (`Sim`) owns game state, and a
Godot 4.7 .NET project (`Game`) renders it. Moon and Mars are baked and playable.

## Commands

```powershell
dotnet build SpaceProgramManager.slnx
dotnet test Sim.Tests/Sim.Tests.csproj     # expect 160 passing
```

Godot (machine-specific path):

```powershell
& 'D:\LTC Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe' --path Game
```

User arguments go **after `--`**:

```powershell
& $godot --path Game -- --region=589971619905011711 --view=management --shot=C:\tmp\shot.png
```

Select a baked body with `--body=moon|mars`. Headless CI uses
`--frame-budget=<ms> --frames=<n>`; the gate prints p95 and exits 1 on failure.
See `docs/development.md` for the full automation workflow.

## Hard rules

1. **`Sim/` must never reference Godot.** It is the deterministic, testable core.
   Engine-facing code belongs in `Game/Scripts/`.
2. **Do not commit `data/`** (gitignored). Tests that need the real bake skip
   gracefully when it is absent; keep that behaviour.
3. **Do not change the save schema without a migration plan.** `docs/save-format.md`
   is the contract. Unknown or incompatible saves must never be overwritten.
   `slot.json` is a rebuildable cache plus UI state, never authoritative.
4. **Placement/removal goes through `CampaignState`.** Views are renderers; they
   must not own building state. Persistence goes through `CampaignService`.
5. **Keep the bake fingerprint gate intact.** A save is only valid for the bake
   it was created against (`bodyKey`, resolutions, region count, `generatedUtc`).
6. **Godot `.uid` files are tracked.** Commit them alongside new scripts.

## Conventions

- C# `net8.0`, nullable enabled, implicit usings, file-scoped namespaces.
- Sim namespaces mirror folders (`Sim.Campaign`, `Sim.World`, …).
- Tests: xUnit in `Sim.Tests/`, one file per subsystem, descriptive method names.
- Commits: imperative subject line, body explaining *why* and how it was verified.
- Docs: English. Keep `README.md`, `docs/`, and the working plan in sync when
  behaviour or milestones change.

## Gotchas

- The working tree uses **LF**; Git may warn that LF will become CRLF. Do not
  rewrite whole files with PowerShell `Get-Content -Raw | Set-Content` — it has
  caused encoding damage in the past.
- `--rot` only applies in the colony view; `--yaw` is the globe camera.
- `colony:` logs `camera 0°` because `Load` runs before `--rot` is applied. That
  is expected; the later `camera: <n>°` line is the effective angle.
- Screenshot runs need a few frames; `--shot` already waits and quits.
- The globe loads all 41,162 regions; the runtime holds one region tile at a time.

## Documentation map

| Document | Use it for |
|---|---|
| `docs/architecture.md` | How the pieces fit, coordinates, data flow |
| `docs/save-format.md` | Save layout and invariants |
| `docs/roadmap.md` | What is next and what "done" means |
| `docs/development.md` | Build, test, automation, pipeline, troubleshooting |
| `docs/decisions.md` | Why past choices were made |
| `~/.opencode/plan/game_development_plan.md` | Full design spec (mirrors the roadmap; lives outside the repo) |

## When you finish

- Run build + tests.
- If you changed behaviour, update the relevant doc and the roadmap status.
- Do not push unless asked; report the commit hash and verification results.
