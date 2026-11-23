# Seamless H3 LOD experiment

**Status:** stages A–C, the D50 five-tier refinement and the D51 seam underlay
are implemented, measured, not ported to `main`.
**Branch:** `experiment/seamless-h3-lod` in the `seamless-h3-lod` worktree.
**Brief:** `~/.opencode/plan/experiment_seamless_h3_lod.md`.

## What exists

`Game/Scripts/LodProbe.cs` is an opaque experimental overlay enabled by `--lod`.
It derives five tiers with the same H3 library the bake uses:

| Tier | Resolution | Cells per parent | Core radius | Parents in core / outer ring |
|---:|---:|---:|---:|---:|
| 0 | 3 | 1 | 4 | 61 / 0 |
| 1 | 4 | 7 | 4 | 61 / 30 |
| 2 | 5 | 49 | 4 | 61 / 30 |
| 3 | 6 | 343 | 2 | 19 / 18 |
| 4 | 7 | 2,401 | 1 | 7 / 12 |

The core of Res 3 parents around the focus is drawn at the selected tier; one
outer ring is drawn one tier coarser. This keeps the density step at 7× between
neighbouring rings instead of the original 343× jump from Res 3 straight to Res 6.
The tier-dependent radius keeps a full rebuild inside the measured budget: Res 4/5
use 61 parents, Res 6 uses 19 and Res 7 uses 7.

Tiers are selected automatically from logarithmic zoom. Up thresholds are spaced
geometrically across zoom 1–128; down thresholds are 5% lower to stop jitter
retriggering a fade:

| Transition | Up | Down |
|---|---:|---:|
| 0 → 1 / 1 → 0 | ≈2.65 | ≈2.52 |
| 1 → 2 / 2 → 1 | ≈6.98 | ≈6.63 |
| 2 → 3 / 3 → 2 | ≈18.45 | ≈17.53 |
| 3 → 4 / 4 → 3 | ≈48.77 | ≈46.33 |

The coarse tier stays opaque underneath while the finer tier fades over it with
a 220 ms smoothstep. This removes the mid-crossfade brightness dip of a
two-way cross-dissolve; an interrupted fade restarts from the visually dominant
tier. Fine-tier fill variation is flattened from ±14% to ±5% around the parent
biome colour. Tier builds are queued and drained against an 8 ms per-frame
budget, with a 96-parent LRU so a short pan-back is a cache hit.

A single coalesced Res 3 underlay mesh fills the active parent window. It is
built from the same real H3 boundary vectors the globe uses in D48, projected
through the same gnomonic basis, and sits below every tier node. Adjacent tier
meshes are still separate `MeshInstance2D` nodes, so independent edge
rasterisation and mixed-tier T-junctions can leave sub-pixel cracks; the underlay
makes those cracks show parent colour instead of the dark canvas. No blur, MSAA,
triangle expansion or per-body rule is involved.

All tiers share one fixed gnomonic projection basis. A boundary shared by two
parents consists of the same H3 vertices on both sides, so it projects to the
same screen polyline even when the parents are at different tiers. That is the
geometric seam fix for this experiment; the underlay covers the raster-level
remainder.

The probe makes no claim about simulation fidelity:

- Res 4–7 are **visual-only**. `Sim`, the save schema, the bake manifest and the
  fingerprint gate are unchanged.
- Fine tiers have no biome samples, so their fills are a small deterministic
  brightness variation of the parent's dominant-biome colour. Enough to judge
  geometry, seams and frame cost, not geography.
- The probe covers the normal views; those views still render behind it. The
  measurement therefore includes a constant baseline cost, which is acceptable
  for comparing tiers but is not a lower bound for a production LOD view.
- The gnomonic anchor is fixed at startup. Panning far from it increases
  distortion; a production version would need reprojection or a globe camera.
- Entering the pseudo-heightmesh "3D" presentation remains the existing Area
  view. There is no resolution button: tier selection is automatic from zoom.

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

# Freeze a mid-crossfade for inspection: zoom 12 = Res 4→5,
# zoom 20 = Res 5→6, zoom 60 = Res 6→7.
& $godot --path Game -- --body=earth --save=<slot> --nosave --fresh `
  --lod --region=590241962326491135 --lod-zoom=20 --lod-fade=0.5 `
  --shot=<path.png>
```

Flags: `--lod-zoom=<1..128>` initial zoom, `--lod-sweep=<seconds>` logarithmic
zoom loop, `--lod-stress=<seconds>` scripted pan loop, `--lod-fade=<0..1>` held
crossfade, `--lod-pan-x/y=<pixels>` deterministic starting pan.

## Measurements (2026-11-23, Earth, region `590241962326491135`)

### Stages A–B — baseline

- Stage A built `1 / 343 / 2401` cells with a 220 ms crossfade; 300-frame p95
  `6.94 ms`.
- Stage B added a 7-parent window, 16-entry LRU and shared projection; 1,200-frame
  p95 `6.94 ms`, max tile load `21 ms`, working set `276–306 MB`.

### D50 — five tiers, overlay fades, tier-dependent window

- Final 1,200-frame run with `--lod-stress=6 --lod-sweep=4`:
  `avg 8.52 ms · p95 16.67 ms · limit 20.00 ms · PASS`. An earlier identical run
  measured `avg 8.09 ms · p95 14.07 ms`, so the larger window has run-to-run
  variance even though both runs pass.
- Two complete `0→1→2→3→4→0` tier cycles logged. Hysteresis held each tier
  through ordinary zoom jitter; the only large jump was the sweep wrapping from
  zoom 128 back to 1.
- Max queued build backlog `23` parents. Headless per-tile loads peaked at
  `27 ms`; non-headless initial Res 7 builds were `35–44 ms` each, still under
  the 100 ms tile-load target.
- `2,220` loads / `1,503` evictions, `185` focus changes over the final stress
  run. The 96-parent LRU capped the cache; working set stayed `276–313 MB`
  headless (`488–517 MB` with the editor window open) with no upward trend.
- Non-headless captures at zoom 12, 20 and 60 with `--lod-fade=0.5` show the
  held coarse-under/fine-over crossfade. A central 200×200 px box contained zero
  background pixels in every capture. Mean luminance differed by less than 0.5%
  between a held 50% fade and its settled tier (zoom 12: `133.7` vs `133.3`;
  zoom 60: `70.4` vs `70.1`), confirming the brightness dip is gone.

### D51 — coalesced Res 3 seam underlay

- Underlay meshes contain `91 / 37 / 19` parents (`1,638 / 666 / 342` vertices)
  at zoom 12 / 20 / 60, rebuilt only when the wanted parent set changes.
- Background pixels inside the sampled world region (x 350–1550, y 80–860)
  before → after: zoom 12 `328,890 → 324,183`, held Res 5→6 fade
  `280,564 → 275,053`, zoom 60 `770 → 0`, held Res 6→7 fade `770 → 0`.
  The zoom-60 result is the mixed-tier parent boundary case that previously
  showed the dark canvas through the seam.
- Final 1,200-frame stress: `avg 8.12 ms · p95 14.99 ms · limit 20.00 ms · PASS`,
  max tile load `21 ms`, peak queue `15`, `2,041` loads / `1,344` evictions,
  working set `279–320 MB` with no upward trend.
- Full verification: clean build, 212/212 C#, 22/22 pipeline, 32/32 headless
  smokes (worst p95 7.14 ms).

VRAM was not measured directly; GPU memory needs an external tool. The 8 GB RAM
ceiling is met by the working-set figure, but that excludes GPU allocations.

## Stage C / D50 / D51 decision

**REVISE, do not port** (D49–D51 in [`decisions.md`](decisions.md)). The
presentation pop and the remaining background-through-seam problem are now
mitigated, and the crossfade, mixed-tier seams, load latency and frame gates all
pass. Res 4–7 detail is still visual-only, the anchor is fixed, VRAM is unmeasured
and the timings include the normal views behind the probe. A further iteration
needs real detail samples, reprojection, VRAM instrumentation and isolated frame
measurement before any port.
