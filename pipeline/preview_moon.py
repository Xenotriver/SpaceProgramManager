"""Dev tool: render the baked Moon tiles to equirectangular preview PNGs.

    python preview_moon.py                 # data/moon -> data/moon/preview_*.png

Renders a coarse lat/lon image by painting each fine hex with its biome colour
(and, separately, its crater-overlay class).  Purely for eyeballing the bake -
it is not part of the game runtime.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

from h3.api import basic_int as h3  # noqa: E402

from spm.hexify import enumerate_grid  # noqa: E402

# Distinct-ish colours for the ten Luna biomes.
BIOME_RGB = np.array(
    [
        [60, 60, 70],      # Mare - dark grey
        [120, 130, 120],   # Lowlands
        [160, 155, 130],   # Midlands
        [210, 200, 170],   # Highlands
        [90, 130, 160],    # N pole lowlands
        [120, 160, 190],   # N pole midlands
        [160, 200, 220],   # N pole highlands
        [130, 90, 120],    # S pole lowlands
        [170, 120, 150],   # S pole midlands
        [210, 160, 190],   # S pole highlands
    ],
    dtype=np.uint8,
)

CRATER_RGB = np.array(
    [
        [20, 20, 20],      # none
        [230, 160, 40],    # single
        [220, 60, 40],     # multi
    ],
    dtype=np.uint8,
)


def load_tiles(out_dir: Path):
    manifest = json.loads((out_dir / "manifest.json").read_text())
    rec = manifest["recordBytes"]
    hdr = manifest["tileHeaderBytes"]
    data = np.fromfile(out_dir / manifest["tilesFile"], dtype=np.uint8)

    n = sum(r["hexCount"] for r in manifest["regions"])
    biome = np.empty(n, dtype=np.uint8)
    crater = np.empty(n, dtype=np.uint8)
    elevation = np.empty(n, dtype=np.int16)

    pos = 0
    for r in manifest["regions"]:
        seg = data[r["offset"] + hdr : r["offset"] + r["length"]].reshape(-1, rec)
        k = seg.shape[0]
        biome[pos : pos + k] = seg[:, 0]
        crater[pos : pos + k] = seg[:, 3]
        elevation[pos : pos + k] = seg[:, 1:3].copy().view("<i2").reshape(-1)
        pos += k
    return manifest, biome, crater, elevation


def render(manifest, values, palette, out_path: Path, width: int = 2048):
    grid = enumerate_grid(manifest["regionResolution"], manifest["fineResolution"])
    height = width // 2
    img = np.zeros((height, width, 3), dtype=np.uint8)

    n = grid.total
    print(f"  projecting {n} hexes ...", flush=True)
    for i in range(n):
        lat, lon = h3.cell_to_latlng(int(grid.hex_ids[i]))
        row = int((90.0 - lat) / 180.0 * height)
        col = int((lon + 180.0) / 360.0 * width)
        if 0 <= row < height and 0 <= col < width:
            img[row, col] = palette[int(values[i])]

    from PIL import Image

    Image.fromarray(img).save(out_path)
    print(f"  wrote {out_path}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default=str(Path(__file__).resolve().parent.parent / "data" / "moon"))
    args = ap.parse_args(argv)

    out_dir = Path(args.out)
    manifest, biome, crater, elevation = load_tiles(out_dir)
    print(f"loaded {biome.size} hexes from {out_dir}")
    render(manifest, biome, BIOME_RGB, out_dir / "preview_biome.png")
    render(manifest, crater, CRATER_RGB, out_dir / "preview_crater.png")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
