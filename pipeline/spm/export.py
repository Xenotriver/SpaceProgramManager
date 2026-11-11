"""Write region tiles and the body manifest.

Layout (see :mod:`spm.config`)::

    data/<body>/manifest.json     body metadata, biome table, region index
    data/<body>/fine.bin          concatenated per-region tiles

Every region tile is a small header followed by one fixed-size record per
fine hex, in the order produced by H3 ``cellToChildren``.  The runtime
regenerates the child ids from the region index, so ids are not stored.
"""

from __future__ import annotations

import json
import struct
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from . import config
from .bodies import BodyDef
from .hexify import Grid


def _record_block(
    dominant: np.ndarray,
    elevation_m: np.ndarray,
    crater_kinds: np.ndarray,
) -> bytes:
    n = dominant.size
    rec = np.zeros((n, config.RECORD_BYTES), dtype=np.uint8)
    rec[:, 0] = dominant.astype(np.uint8)
    elev = np.clip(np.rint(elevation_m), -32768, 32767).astype("<i2")
    rec[:, 1:3] = elev.view(np.uint8).reshape(n, 2)
    rec[:, 3] = crater_kinds.astype(np.uint8)
    return rec.tobytes()


def write_tiles(
    body: BodyDef,
    grid: Grid,
    *,
    dominant: np.ndarray,
    elevation_m: np.ndarray,
    crater_kinds: np.ndarray,
    region_dominant: np.ndarray,
    out_dir: Path | None = None,
    sources: dict[str, str] | None = None,
) -> Path:
    """Write ``manifest.json`` + ``fine.bin`` and return the manifest path."""
    out_dir = out_dir or (config.DATA_DIR / body.key)
    out_dir.mkdir(parents=True, exist_ok=True)
    bin_path = out_dir / "fine.bin"

    region_entries = []
    offset = 0
    with open(bin_path, "wb") as fh:
        for r, region in enumerate(grid.region_ids):
            start, end = grid.region_slice(r)
            hex_count = end - start
            header = struct.pack(
                "<4sHHHHII",
                config.TILE_MAGIC,
                config.TILE_FORMAT_VERSION,
                grid.fine_resolution,
                grid.region_resolution,
                0,
                hex_count,
                0,
            )
            body_bytes = _record_block(
                dominant[start:end],
                elevation_m[start:end],
                crater_kinds[start:end],
            )
            fh.write(header)
            fh.write(body_bytes)
            length = len(header) + len(body_bytes)
            region_entries.append(
                {
                    "index": str(int(region)),
                    "dominantBiome": int(region_dominant[r]),
                    "hexCount": hex_count,
                    "offset": offset,
                    "length": length,
                }
            )
            offset += length

    manifest = {
        "format": "spm-region-tiles",
        "version": config.TILE_FORMAT_VERSION,
        "generatedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "body": {
            "key": body.key,
            "displayName": body.display_name,
            "radiusKm": body.radius_km,
        },
        "fineResolution": grid.fine_resolution,
        "regionResolution": grid.region_resolution,
        "recordBytes": config.RECORD_BYTES,
        "tileHeaderBytes": config.TILE_HEADER_BYTES,
        "tilesFile": bin_path.name,
        "biomes": [
            {
                "code": code,
                "id": biome.id,
                "displayName": biome.display_name,
                "elevationRank": biome.elevation_rank,
                "buildable": biome.buildable,
            }
            for code, biome in enumerate(body.biomes)
        ],
        "sources": sources or {},
        "regions": region_entries,
    }

    manifest_path = out_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=None), encoding="utf-8")
    return manifest_path
