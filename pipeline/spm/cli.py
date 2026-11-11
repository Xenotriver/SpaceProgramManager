"""Command-line orchestration for the data pipeline."""

from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np

from . import classify, crater, export, fetch, hexify, raster
from .bodies import get_body

# LOLA GDR grids are simple-cylindrical, longitude 0..360 east-positive,
# latitude 90..-90, pixel registered.
LOLA_LON_WEST = 0.0
LOLA_LAT_NORTH = 90.0

# The USGS geologic raster is simple-cylindrical, longitude -180..180.
UGM_LON_WEST = -180.0


def _log(message: str) -> None:
    print(f"[pipeline] {message}", flush=True)


def _make_sampler(biome: np.ndarray, dlon: float, dlat: float):
    height, width = biome.shape

    def sample(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
        row = np.floor((LOLA_LAT_NORTH - lat) / dlat).astype(np.int64)
        col = np.floor((np.mod(lon, 360.0) - LOLA_LON_WEST) / dlon).astype(np.int64)
        np.clip(row, 0, height - 1, out=row)
        np.clip(col, 0, width - 1, out=col)
        return biome[row, col]

    return sample


def _raster_blocks(
    lat: np.ndarray,
    lon: np.ndarray,
    biome: np.ndarray,
    elevation: np.ndarray,
    block_rows: int,
):
    height = biome.shape[0]
    for start in range(0, height, block_rows):
        end = min(start + block_rows, height)
        yield (
            lat[start:end].ravel(),
            lon[start:end].ravel(),
            biome[start:end].ravel(),
            elevation[start:end].ravel(),
        )


def run(args: argparse.Namespace) -> Path:
    body = get_body(args.body)
    fine_res = args.fine_res or body.fine_resolution
    region_res = args.region_res or body.region_resolution

    t0 = time.time()
    _log(f"body={body.key} dem={args.dem}ppd fine_res={fine_res} region_res={region_res}")
    _log("fetching inputs (cached)")
    paths = fetch.ensure_moon_inputs(args.dem, force=args.force)

    _log("reading DEM")
    elevation = raster.read_lola_img(paths["dem"], paths["dem_label"])
    height, width = elevation.shape
    dlat = 180.0 / height
    dlon = 360.0 / width

    _log("reading geology (resampled + aligned to DEM grid)")
    geology = raster.read_geology(
        paths["geology"],
        out_shape=elevation.shape,
        lon_west_in=UGM_LON_WEST,
        lon_west_out=LOLA_LON_WEST,
    )

    _log("building lat/lon grid")
    lat, lon = raster.global_latlon(
        height, width, lon_west=LOLA_LON_WEST, lat_north=LOLA_LAT_NORTH
    )

    _log("classifying surface")
    biome = classify.classify_surface(body, geology, elevation, lat)

    _log("enumerating H3 grid")
    grid = hexify.enumerate_grid(region_res, fine_res)
    _log(f"  {grid.region_ids.size} regions, {grid.total} fine hexes")

    _log("aggregating pixels -> hexes")
    blocks = _raster_blocks(lat, lon, biome, elevation, args.block_rows)
    agg = hexify.aggregate_pixels(grid, blocks, resolution=fine_res, jobs=args.jobs)

    _log("resolving empty / tied hexes")
    sampler = _make_sampler(biome, dlon, dlat)
    agg = hexify.fill_empty_and_ties(
        grid, agg, sample_fn=sampler, priority=list(range(len(body.biomes)))
    )

    dominant = agg.dominant.astype(np.int8, copy=True)
    crater_kinds = np.zeros(grid.total, dtype=np.uint8)

    if not args.skip_craters:
        _log("building crater overlay (Robbins 2019)")
        craters = crater.load_robbins(paths["craters"], min_diameter_km=args.min_crater_km)
        _log(f"  {len(craters)} craters >= {args.min_crater_km} km")
        overlay = crater.build_overlay(
            body, craters, resolution=fine_res, rim_rings=args.rim_rings
        )
        idx = grid.lookup(overlay.hexes)
        np.maximum.at(crater_kinds, idx, overlay.kinds)

    _log("rolling up regions")
    region_dom = hexify.region_dominant(grid, dominant, priority=list(range(len(body.biomes))))

    _log("exporting tiles")
    manifest = export.write_tiles(
        body,
        grid,
        dominant=dominant,
        elevation_m=agg.elevation_m,
        crater_kinds=crater_kinds,
        region_dominant=region_dom,
        out_dir=Path(args.out) if args.out else None,
        sources={
            "dem": f"LOLA GDR LDEM_{args.dem} (NASA PGS, public domain)",
            "geology": "USGS Unified Geologic Map of the Moon 2020, 64ppd raster (CC0)",
            "craters": "Robbins (2019) lunar crater catalogue (USGS Astrogeology)",
        },
    )
    _log(f"done in {time.time() - t0:.1f}s -> {manifest}")
    return manifest


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="spm-bake", description=__doc__)
    parser.add_argument("--body", default="moon", help="body key (default: moon)")
    parser.add_argument(
        "--dem",
        type=int,
        default=16,
        choices=[4, 16, 64],
        help="LOLA GDR resolution in pixels/degree (default: 16)",
    )
    parser.add_argument("--fine-res", type=int, default=None, help="override fine H3 resolution")
    parser.add_argument("--region-res", type=int, default=None, help="override region H3 resolution")
    parser.add_argument(
        "--min-crater-km",
        type=float,
        default=1.0,
        help="minimum crater diameter to include in the overlay (default: 1.0)",
    )
    parser.add_argument(
        "--rim-rings",
        type=int,
        default=0,
        help="half-width of the multi-cell crater rim band in hex rings "
        "(0 = a single ring; default: 0)",
    )
    parser.add_argument("--jobs", type=int, default=1, help="parallel workers for pixel->hex")
    parser.add_argument("--block-rows", type=int, default=512, help="raster rows per aggregation block")
    parser.add_argument("--skip-craters", action="store_true", help="skip the crater overlay")
    parser.add_argument("--out", default=None, help="output directory (default: data/<body>)")
    parser.add_argument("--force", action="store_true", help="re-download sources")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    run(args)
    return 0
