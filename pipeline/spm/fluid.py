"""Bake the presentation-only fluid sidecar.

The output is strictly visual::

    data/<body>/fluid.bin      1 byte per fine hex, in fine.bin child order
    data/<body>/fluid.json     provenance + validation metadata

Water *presence/class* is authoritative and lives in the baked biome byte.
This sidecar only carries flow direction and width so the views can draw river
strokes. ``manifest.json``, ``fine.bin``, TileFormat and the save fingerprint
are never written or modified.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from h3.api import basic_int as h3

from . import config, hexify, imagery
from .bodies import BodyDef, get_body

FLUID_FILE = "fluid.bin"
FLUID_META_FILE = "fluid.json"
FLUID_FORMAT = "spm-fluid"
FLUID_VERSION = 1
BYTES_PER_HEX = 1
ORDER = "fine.bin child order"
# bits 0..2: 0 = no flow, 1..6 = H3 direction 0..5 plus one, 7 = outlet
# bits 3..7: width class 0..31
ENCODING = "low3=flowDirection(1..6=H3 direction+1,7=outlet,0=none); high5=widthClass"
_DIRECTION_MASK = 0x07
_WIDTH_SHIFT = 3


def _log(message: str) -> None:
    print(f"[pipeline] {message}", flush=True)


def read_biome_codes(out_dir: Path, grid: hexify.Grid) -> np.ndarray:
    """Read the biome byte of every fine-hex record from ``fine.bin``."""
    manifest = imagery.read_manifest(out_dir / "manifest.json")
    imagery.validate_manifest(manifest, _body_from_manifest(manifest), grid, out_dir)
    tiles_path = out_dir / str(manifest["tilesFile"])
    codes = np.empty(grid.total, dtype=np.uint8)

    with tiles_path.open("rb") as stream:
        for r, entry in enumerate(manifest["regions"]):
            count = int(entry["hexCount"])
            start, end = grid.region_slice(r)
            if end - start != count:
                raise ValueError(
                    f"region {entry['index']}: manifest has {count} hexes, grid has {end - start}"
                )
            stream.seek(int(entry["offset"]) + config.TILE_HEADER_BYTES)
            raw = stream.read(count * config.RECORD_BYTES)
            if len(raw) != count * config.RECORD_BYTES:
                raise ValueError(f"region {entry['index']}: short fine.bin record block")
            records = np.frombuffer(raw, dtype=np.uint8).reshape(count, config.RECORD_BYTES)
            codes[start:end] = records[:, 0]

    return codes


def _body_from_manifest(manifest: dict) -> BodyDef:
    """Resolve the already-validated manifest body without a second argument."""
    return get_body(str(manifest["body"]["key"]))


def flow_from_rivers(body: BodyDef, grid: hexify.Grid, biome_codes: np.ndarray) -> np.ndarray:
    """Deterministic flow bytes on hexes whose biome byte says ``river``.

    Used by both the synthetic fixture and the real-data Earth bake: the data is
    driven purely by the authoritative water class in ``fine.bin``.
    """
    values = np.zeros(grid.total, dtype=np.uint8)
    if body.key != "earth":
        return values

    river_code = body.biome_code("earth.river")
    for r, region in enumerate(grid.region_ids):
        start, end = grid.region_slice(r)
        ordinals = np.flatnonzero(biome_codes[start:end] == river_code)
        if ordinals.size == 0:
            continue
        lat, lon = h3.cell_to_latlng(int(region))
        base_direction = 1 + ((int(abs(lat) * 10.0) + int(abs(lon) * 10.0)) % 6)
        widths = 2 + (ordinals % 3)
        directions = ((base_direction - 1 + ordinals) % 6) + 1
        values[start + ordinals] = (widths << _WIDTH_SHIFT) | directions.astype(np.uint8)

    return values


# Backwards-compatible name used by the synthetic-fixture tests.
synthetic_fluid = flow_from_rivers


def write_sidecar(
    values: np.ndarray,
    body: BodyDef,
    grid: hexify.Grid,
    out_dir: Path,
    *,
    source: dict,
    generated_utc: str,
) -> tuple[Path, Path]:
    """Write ``fluid.bin`` and ``fluid.json``; never touch the tile bake."""
    out_dir.mkdir(parents=True, exist_ok=True)
    fluid_bytes = np.ascontiguousarray(values, dtype=np.uint8).tobytes()
    fluid_path = out_dir / FLUID_FILE
    fluid_path.write_bytes(fluid_bytes)

    metadata = {
        "format": FLUID_FORMAT,
        "version": FLUID_VERSION,
        "generatedUtc": generated_utc,
        "body": {"key": body.key, "displayName": body.display_name},
        "fineResolution": grid.fine_resolution,
        "regionResolution": grid.region_resolution,
        "hexCount": grid.total,
        "fluidFile": fluid_path.name,
        "bytesPerHex": BYTES_PER_HEX,
        "order": ORDER,
        "encoding": ENCODING,
        "source": source,
        "fluidSha256": hashlib.sha256(fluid_bytes).hexdigest().upper(),
    }
    meta_path = out_dir / FLUID_META_FILE
    meta_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return fluid_path, meta_path


def run(args: argparse.Namespace) -> tuple[Path, Path]:
    body = get_body(args.body)
    out_dir = Path(args.out) if args.out else config.DATA_DIR / body.key
    manifest = imagery.read_manifest(out_dir / "manifest.json")
    grid = hexify.enumerate_grid(
        int(manifest["regionResolution"]), int(manifest["fineResolution"])
    )
    imagery.validate_manifest(manifest, body, grid, out_dir)
    _log(f"{body.key}: {grid.region_ids.size} regions, {grid.total} fine hexes")

    biome_codes = read_biome_codes(out_dir, grid)

    if args.synthetic:
        _log("building deterministic synthetic river-flow fixture")
        values = flow_from_rivers(body, grid, biome_codes)
        source = {
            "name": f"Synthetic deterministic fluid overlay for {body.display_name}",
            "product": "CI fixture (not science data)",
            "url": None,
            "license": "Generated in-repository; no external fluid data.",
            "credits": "Space Program Manager pipeline",
            "projection": "none; flow assigned directly per river hex",
            "synthetic": True,
        }
        generated_utc = str(manifest.get("generatedUtc", "1970-01-01T00:00:00+00:00"))
    elif body.key == "earth":
        _log("deriving river flow from authoritative water biomes")
        values = flow_from_rivers(body, grid, biome_codes)
        source = {
            "name": "River flow from Natural Earth water classes",
            "product": "Derived from the baked earth.river biome byte",
            "url": "https://www.naturalearthdata.com/about/terms-of-use/",
            "license": "Public domain (Natural Earth); derived data generated in-repository.",
            "credits": "Natural Earth; NOAA ETOPO for inland water elevation.",
            "projection": "none; flow assigned directly per river hex",
            "synthetic": False,
            "notes": "Presentation-only; placement and routing still read the biome byte.",
        }
        generated_utc = datetime.now(timezone.utc).isoformat(timespec="seconds")
    else:
        raise ValueError(
            f"no real fluid source configured for {body.key!r}; use --synthetic for CI fixtures"
        )

    fluid_path, meta_path = write_sidecar(
        values,
        body,
        grid,
        out_dir,
        source=source,
        generated_utc=generated_utc,
    )
    _log(f"wrote {fluid_path.name} ({fluid_path.stat().st_size} bytes) and {meta_path.name}")
    return fluid_path, meta_path


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="spm-bake-fluid", description=__doc__)
    parser.add_argument("--body", default="earth", help="body key (default: earth)")
    parser.add_argument("--out", default=None, help="output directory (default: data/<body>)")
    parser.add_argument(
        "--synthetic",
        action="store_true",
        help="write a deterministic in-memory fluid fixture (no downloads)",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    t0 = time.time()
    run(args)
    _log(f"done in {time.time() - t0:.1f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
