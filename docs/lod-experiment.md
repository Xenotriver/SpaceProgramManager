# Seamless H3 LOD experiment

**Status:** stages A and B implemented, measured, not ported to `main`.
**Branch:** `experiment/seamless-h3-lod` in the `seamless-h3-lod` worktree.
**Brief:** `~/.opencode/plan/experiment_seamless_h3_lod.md`.

## What exists

`Game/Scripts/LodProbe.cs` is an opaque experimental overlay enabled by `--lod`.
It derives three tiers with the same H3 library the bake uses:

| Tier | Resolution | Cells | Role |
|---:|---:|---:|---|
| 0 | 3 | 1 | aggregate parent |
| 1 | 6 | 343 | shipped fine grid |
| 2 | 7 | 2,401 | derived detail |

The focus Res 3 parent is drawn at the zoom-selected tier; its `gridDisk(1)`
neighbours are drawn one tier coarser. Crossing a zoom threshold starts a
220 ms smoothstep crossfade between the outgoing and incoming meshes. Panning
changes the focus; new parents load lazily and a 16-entry LRU keeps recently
used parents for a quick pan-back. Wheel zooms, right-drag pans.

All tiers share one fixed gnomonic projection basis. A boundary shared by two
parents consists of the same H3 vertices on both sides, so it projects to the
same screen polyline even when the parents are at different tiers. That is the
seam fix for this experiment — no blur, MSAA or per-body special case.

The probe makes no claim about simulation fidelity:

- Res 7 is **visual-only**. `Sim`, the save schema, the bake manifest and the
  fingerprint gate are unchanged.
- Res 6/7 cells have no biome samples, so their fills are a deterministic
  brightness variation of the parent's dominant-biome colour. Enough to judge
  geometry, seams and frame cost, not geography.
- The probe covers the normal views; those views still render behind it. The
  measurement therefore includes a constant baseline cost, which is acceptable
  for comparing tiers but is not a lower bound for a production LOD view.
- The gnomonic anchor is fixed at startup. Panning far from it increases
  distortion; a production version would need reprojection or a globe camera.

## Run it

The worktree has no `data/` directory. Point `SPM_DATA` at the main checkout:

```powershell
$env:SPM_DATA = 'D:\OpenCodeProjects\SpaceProgramManager\data'
$godot = 'D:\LTC Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe'

# Mixed-tier parent boundary screenshot (non-headless; headless --shot hangs).
& $godot --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-zoom=60 --lod-pan-x=160 `
  --shot=<path.png>

# Full pan + zoom stress with the frame gate.
& $godot --headless --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-stress=6 --lod-sweep=4 `
  --frame-budget=20 --frames=1200

# Freeze a 50% crossfade for inspection.
& $godot --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-zoom=12 --lod-fade=0.5 `
  --shot=<path.png>
```

Flags: `--lod-zoom=<1..128>` initial zoom, `--lod-sweep=<seconds>` logarithmic
zoom loop, `--lod-stress=<seconds>` scripted pan loop, `--lod-fade=<0..1>` held
crossfade, `--lod-pan-x/y=<pixels>` deterministic starting pan.

## Measurements (2026-11-22, Earth, region `590241962326491135`)

### Stage A — one parent

- Tiers built: `1 / 343 / 2401` cells, crossfades `0→1`, `1→2`, `2→0`.
- 300-frame sweep: `avg 6.93 ms · p95 6.94 ms · limit 20.00 ms · PASS`.
- Working set ~301–336 MB.

### Stage B — seven parents, LRU, pan + zoom stress

- 1,200 frames with `--lod-stress=6 --lod-sweep=4`:
  `avg 7.06 ms · p95 6.94 ms · limit 20.00 ms · PASS`.
- Two complete `0→1→2→0` tier cycles logged.
- Tile load: 2–6 ms at Res 6, 20–21 ms at Res 7 (2,401 cells) — comfortably
  under the 100 ms tile-load target.
- 333 loads / 271 evictions, 136 focus changes over 8 s; LRU cache held at 16
  parents. Working set stayed between 276 MB and 306 MB with no upward trend.
- Non-headless captures at zoom 60 (focus Res 7, neighbours Res 6) and zoom 12
  (focus Res 6, neighbours Res 3) show full coverage through the parent
  boundaries; a 200×200 px central box contained zero background pixels.

VRAM was not measured directly; GPU memory needs an external tool. The 8 GB RAM
ceiling is met by the working-set figure, but that excludes GPU allocations.

## Stage C decision

**REVISE, do not port** (D49 in [`decisions.md`](decisions.md)). The crossfade,
mixed-tier seams, load latency and memory gates all pass, but Res 6/7 detail is
visual-only, the gnomonic anchor is fixed, VRAM is unmeasured and the timings
include the normal views behind the probe. A further iteration needs real detail
samples, reprojection and isolated frame measurement before any port.
