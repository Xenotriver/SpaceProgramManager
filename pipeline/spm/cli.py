"""Command-line orchestration for the data pipeline."""

from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np
from h3.api import basic_int as h3

from . import classify, crater, export, fetch, hexify, raster
from .bodies import BodyDef, get_body

# LOLA GDR grids are simple-cylindrical, longitude 0..360 east-positive,
# latitude 90..-90, pixel registered.
LOLA_LON_WEST = 0.0
LOLA_LAT_NORTH = 90.0

# The USGS geologic raster is simple-cylindrical, longitude -180..180.
UGM_LON_WEST = -180.0


def _log(message: str) -> None:
    print(f"[pipeline] {message}", flush=True)


def _make_sampler(values: np.ndarray, dlon: float, dlat: float):
    height, width = values.shape

    def sample(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
        row = np.floor((LOLA_LAT_NORTH - lat) / dlat).astype(np.int64)
        col = np.floor((np.mod(lon, 360.0) - LOLA_LON_WEST) / dlon).astype(np.int64)
        np.clip(row, 0, height - 1, out=row)
        np.clip(col, 0, width - 1, out=col)
        return values[row, col]

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


def _read_real_inputs(
    body: BodyDef,
    args: argparse.Namespace,
) -> tuple[np.ndarray, np.ndarray | None, dict[str, str], dict[str, Path]]:
    """Fetch and decode the real elevation/geology rasters for ``body``."""
    paths = fetch.ensure_inputs(body, args.dem, force=args.force)

    if body.source_profile == "moon":
        _log("reading DEM")
        elevation = raster.read_lola_img(paths["dem"], paths["dem_label"])
        _log("reading geology (resampled + aligned to DEM grid)")
        geology = raster.read_geology(
            paths["geology"],
            out_shape=elevation.shape,
            lon_west_in=UGM_LON_WEST,
            lon_west_out=LOLA_LON_WEST,
        )
        sources = {
            "dem": f"LOLA GDR LDEM_{args.dem} (NASA PGS, public domain)",
            "geology": "USGS Unified Geologic Map of the Moon 2020, 64ppd raster (CC0)",
            "craters": "Robbins (2019) lunar crater catalogue (USGS Astrogeology)",
        }
        return elevation, geology, sources, paths

    if body.source_profile == "mars":
        _log("reading MOLA MEGDR DEM")
        elevation = raster.read_mola_img(paths["dem"], paths["dem_label"])
        _log("rasterizing USGS geologic units")
        geology = classify.read_mars_geology(paths["geology"], elevation.shape)
        sources = {
            "dem": "MOLA MEGDR 16 ppd (NASA PDS, public domain)",
            "geology": "USGS Mars 15M Geologic Map GIS Renovation, derived from SIM 3292",
            "craters": "Robbins & Hynek (2012) global Mars crater catalogue",
        }
        return elevation, geology, sources, paths

    # fetch.ensure_inputs() already rejects unknown profiles; keep this guard so
    # a future profile cannot silently fall back to Moon readers.
    raise RuntimeError(f"no real raster reader for source profile {body.source_profile!r}")


def _biome_code_or_none(body: BodyDef, biome_id: str) -> int | None:
    return body.biome_code(biome_id) if biome_id in body.biome_ids else None


def _synthetic_biome_code(body: BodyDef, lat: float, lon: float) -> int:
    """Deterministic latitude/longitude biome for a synthetic CI bake."""
    north = body.biome_code(body.pole_north_biome_ids[1])
    south = body.biome_code(body.pole_south_biome_ids[1])

    if lat >= body.pole_latitude_deg:
        return north
    if lat <= -body.pole_latitude_deg:
        return south

    volcanic = _biome_code_or_none(body, "mars.volcanic")
    if volcanic is not None and -150.0 < lon < -100.0 and -10.0 < lat < 40.0:
        return volcanic

    canyon = _biome_code_or_none(body, "mars.canyon")
    if canyon is not None and 30.0 < lon < 100.0 and -25.0 < lat < 10.0:
        return canyon

    mare = _biome_code_or_none(body, body.mare_biome_id or "")
    if mare is not None and -60.0 < lon < 60.0 and -30.0 < lat < 30.0:
        return mare

    if lat < -15.0:
        return body.biome_code(body.highlands_biome_id)
    if lat > 20.0:
        return body.biome_code(body.lowlands_biome_id)
    return body.biome_code(body.midlands_biome_id)


def _synthetic_elevation(body: BodyDef, lat: float, lon: float, ordinal: int) -> float:
    """Deterministic elevation in metres for a synthetic CI bake."""
    north_fraction = (lat + 90.0) / 180.0
    elevation = 2500.0 - 6500.0 * north_fraction

    if _biome_code_or_none(body, "mars.volcanic") is not None:
        if -150.0 < lon < -100.0 and -10.0 < lat < 40.0:
            elevation += 9000.0
        if 30.0 < lon < 100.0 and -25.0 < lat < 10.0:
            elevation -= 4000.0

    return elevation + float((ordinal * 37) % 101) - 50.0


def _run_synthetic(
    body: BodyDef,
    args: argparse.Namespace,
    fine_res: int,
    region_res: int,
) -> Path:
    """Bake the full tile layout without downloads or a source raster.

    At H3 res 6 a small raster would leave almost every hex empty, and the
    generic nearest-pixel fill would call H3 once per empty hex.  Assigning the
    synthetic values directly from each region centroid keeps the fixture fast
    while still producing the real region/hex counts the runtime expects.
    """
    grid = hexify.enumerate_grid(region_res, fine_res)
    _log(f"  {grid.region_ids.size} regions, {grid.total} fine hexes")

    dominant = np.empty(grid.total, dtype=np.int8)
    elevation = np.empty(grid.total, dtype=np.float32)
    crater_kinds = np.zeros(grid.total, dtype=np.uint8)

    for r, region in enumerate(grid.region_ids):
        lat, lon = h3.cell_to_latlng(int(region))
        start, end = grid.region_slice(r)
        count = end - start
        dominant[start:end] = _synthetic_biome_code(body, lat, lon)
        ords = np.arange(count, dtype=np.float32)
        elevation[start:end] = _synthetic_elevation(body, lat, lon, 0) + (
            (ords * 37.0) % 101.0
        ) - 50.0

    region_dom = hexify.region_dominant(grid, dominant, priority=list(range(len(body.biomes))))

    sources = {
        "dem": f"Synthetic deterministic elevation for {body.display_name} "
        "(CI fixture, not science data)",
    }
    if body.has_geology_source:
        sources["geology"] = "synthetic (no external raster)"
    if body.has_crater_source:
        sources["craters"] = "synthetic overlay disabled"

    return export.write_tiles(
        body,
        grid,
        dominant=dominant,
        elevation_m=elevation,
        crater_kinds=crater_kinds,
        region_dominant=region_dom,
        out_dir=Path(args.out) if args.out else None,
        sources=sources,
    )


def run(args: argparse.Namespace) -> Path:
    body = get_body(args.body)
    fine_res = body.fine_resolution if args.fine_res is None else args.fine_res
    region_res = body.region_resolution if args.region_res is None else args.region_res

    t0 = time.time()
    _log(f"body={body.key} dem={args.dem}ppd fine_res={fine_res} region_res={region_res}")

    if args.synthetic:
        _log("generating deterministic in-memory raster (no fetch)")
        manifest = _run_synthetic(body, args, fine_res, region_res)
        _log(f"done in {time.time() - t0:.1f}s -> {manifest}")
        return manifest

    _log("fetching inputs (cached)")
    elevation, geology, sources, paths = _read_real_inputs(body, args)
    height, width = elevation.shape
    dlat = 180.0 / height
    dlon = 360.0 / width

    _log("building lat/lon grid")
    lat, lon = raster.global_latlon(
        height, width, lon_west=LOLA_LON_WEST, lat_north=LOLA_LAT_NORTH
    )

    _log("classifying surface")
    if body.source_profile == "mars":
        biome = classify.classify_mars_surface(body, geology, elevation, lat)
    else:
        biome = classify.classify_surface(body, geology, elevation, lat)

    _log("enumerating H3 grid")
    grid = hexify.enumerate_grid(region_res, fine_res)
    _log(f"  {grid.region_ids.size} regions, {grid.total} fine hexes")

    _log("aggregating pixels -> hexes")
    blocks = _raster_blocks(lat, lon, biome, elevation, args.block_rows)
    agg = hexify.aggregate_pixels(grid, blocks, resolution=fine_res, jobs=args.jobs)

    _log("resolving empty / tied hexes")
    biome_sampler = _make_sampler(biome, dlon, dlat)
    elevation_sampler = _make_sampler(elevation, dlon, dlat)
    agg = hexify.fill_empty_and_ties(
        grid,
        agg,
        biome_sample_fn=biome_sampler,
        elevation_sample_fn=elevation_sampler,
        priority=list(range(len(body.biomes))),
    )

    dominant = agg.dominant.astype(np.int8, copy=True)
    crater_kinds = np.zeros(grid.total, dtype=np.uint8)

    if not args.skip_craters and body.has_crater_source:
        catalogue = "Robbins & Hynek 2012" if body.source_profile == "mars" else "Robbins 2019"
        _log(f"building crater overlay ({catalogue})")
        craters = crater.load_catalogue(paths["craters"], min_diameter_km=args.min_crater_km)
        _log(f"  {len(craters)} craters >= {args.min_crater_km} km")
        overlay = crater.build_overlay(
            body, craters, resolution=fine_res, rim_rings=args.rim_rings
        )
        idx = grid.lookup(overlay.hexes)
        np.maximum.at(crater_kinds, idx, overlay.kinds)
    elif not args.skip_craters:
        _log(f"crater overlay skipped (no configured catalogue for {body.key})")

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
        sources=sources,
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
        help="LOLA GDR resolution in pixels/degree (Moon; Mars real bakes use 16 only; "
        "ignored with --synthetic)",
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
    parser.add_argument(
        "--synthetic",
        action="store_true",
        help="bake a deterministic in-memory fixture (no network, no source data)",
    )
    parser.add_argument("--out", default=None, help="output directory (default: data/<body>)")
    parser.add_argument("--force", action="store_true", help="re-download sources")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    run(args)
    return 0
