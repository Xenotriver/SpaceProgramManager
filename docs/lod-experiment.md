# Seamless H3 LOD experiment — Stage A

**Status:** stage A implemented, measured, not ported to `main`.
**Branch:** `experiment/seamless-h3-lod` in the `seamless-h3-lod` worktree.
**Brief:** `~/.opencode/plan/experiment_seamless_h3_lod.md`.

## What exists

`Game/Scripts/LodProbe.cs` is an opaque experimental overlay enabled by `--lod`.
It derives three tiers from one Res 3 parent with the same H3 library the bake
uses:

| Tier | Resolution | Cells | Role |
|---:|---:|---:|---|
| 0 | 3 | 1 | aggregate parent |
| 1 | 6 | 343 | shipped fine grid |
| 2 | 7 | 2,401 | derived detail |

Tier geometry is built once as three `ArrayMesh` instances. Camera zoom selects
the tier; crossing a threshold starts a 220 ms smoothstep crossfade between the
outgoing and incoming meshes. Wheel zooms, right-drag pans.

Stage A deliberately makes no claim about simulation fidelity:

- Res 7 is **visual-only**. `Sim`, the save schema, the bake manifest and the
  fingerprint gate are unchanged.
- Res 6/7 cells have no biome samples, so their fills are a deterministic
  brightness variation of the parent's dominant-biome colour. This is enough to
  judge geometry, seams and frame cost, not geography.
- The probe covers the normal views; those views still render behind it. The
  measurement therefore includes a constant baseline cost, which is acceptable
  for comparing tiers but is not a lower bound for a production LOD view.

## Run it

The worktree has no `data/` directory. Point `SPM_DATA` at the main checkout:

```powershell
$env:SPM_DATA = 'D:\OpenCodeProjects\SpaceProgramManager\data'
$godot = 'D:\LTC Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe'

# Tier screenshots (non-headless; headless --shot still hangs).
& $godot --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-zoom=12 `
  --shot=<path.png>

# Auto-sweep through all tiers and back, with the frame gate.
& $godot --headless --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-sweep=1.5 `
  --frame-budget=20 --frames=300

# Freeze a 50% crossfade for inspection.
& $godot --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-zoom=12 --lod-fade=0.5 `
  --shot=<path.png>
```

Flags: `--lod-zoom=<1..128>` sets initial zoom, `--lod-sweep=<seconds>` loops a
logarithmic zoom, `--lod-fade=<0..1>` holds a crossfade between the current tier
and the next one.

## Stage A measurements (2026-11-22, Earth, region `590241962326491135`)

- Tiers built: `1 / 343 / 2401` cells.
- Working set after mesh build: ~336 MB; during transitions ~301–305 MB.
- 300-frame auto-sweep across all tier boundaries:
  `avg 6.93 ms · p95 6.94 ms · limit 20.00 ms · PASS`.
- Crossfade log confirmed `0→1`, `1→2`, `2→0` transitions during the sweep.
- Non-headless captures at zoom 1, 12 and 60 show the expected increase in
  visible geometry; a held 50% crossfade at zoom 12 shows both tiers.

VRAM was not measured directly; GPU memory needs an external tool and is a
Stage B/C gate. The 8 GB RAM ceiling is comfortably met by the working-set
figure, but that metric excludes GPU allocations.

## Next

Stage B: active parent + `gridDisk(1)` neighbours, lazy load/LRU eviction,
cross-parent seam handling and a long pan/zoom leak test. Stage C writes the
keep/revise/kill decision with measured numbers. Nothing ports to `main`
without a separate decision.
