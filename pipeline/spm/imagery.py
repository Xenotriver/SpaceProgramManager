"""Bake optional true-colour sidecars without changing the tile bake.

The output of this module is presentation-only::

    data/<body>/color.bin       3 bytes per fine hex, in fine.bin child order
    data/<body>/imagery.json    provenance + validation metadata

``manifest.json``, ``fine.bin``, the tile format and the save fingerprint are
never written or modified.  A missing or corrupt sidecar is handled by the
runtime as a biome-colour fallback.
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
from PIL import Image

from . import config, fetch, hexify, raster, synthetic_earth
from .bodies import BodyDef, get_body

COLOR_FILE = "color.bin"
IMAGERY_FILE = "imagery.json"
IMAGERY_FORMAT = "spm-imagery"
IMAGERY_VERSION = 1
BYTES_PER_HEX = 3

# The bake sampler uses 0..360 east-positive, latitude +90..-90, pixel-centre
# registration.  Both locked imagery sources are -180..180 and north-up.
LON_WEST = 0.0
LAT_NORTH = 90.0

SOURCES: dict[str, dict[str, str]] = {
    "moon": {
        "fetch": "moon_color",
        "name": "NASA SVS CGI Moon Kit 2019 LROC WAC colour",
        "product": "CGI Moon Kit, NASA SVS ID 4720",
        "url": "https://svs.gsfc.nasa.gov/4720/",
        "file": "lroc_color_poles_2k.tif",
        "license": "NASA content is generally not subject to US copyright; acknowledge NASA/SVS.",
        "credits": "NASA/GSFC, ASU; visualisation by Ernie Wright (STC); scientist Noah Petro (NASA/GSFC).",
        "projection": "equirectangular, north up, longitude -180..180, centred on 0E",
        "sha256": "13B797422E8C4B8607FF2B2623AC3A046A6DA0132D567C2D272D92FAD7052C4A",
        "notes": "Adapted from the LROC Hapke-normalized WAC mosaic; polar gaps filled with LDAM albedo. Aesthetic, not scientific.",
    },
    "mars": {
        "fetch": "mars_color",
        "name": "USGS Viking MDIM 2.1 colorized global mosaic 232 m",
        "product": "Viking MDIM2.1 ClrMosaic Global 232m",
        "url": "https://astrogeology.usgs.gov/search/map/mars_viking_colorized_global_mosaic_232m",
        "file": "mars_viking_mdim21_clrmosaic_1km.jpg",
        "license": "Public domain (USGS Astropedia product page).",
        "credits": "NASA/USGS; colorized by NASA Ames from Viking VIS data blended with MDIM 2.1.",
        "projection": "simple cylindrical, north up, planetocentric latitude, positive-east longitude -180..180",
        "sha256": "FDFCD335559C3DC67052B7E8A9565D850E336AC0D1F3EA7F5EB7826FFB44ECB2",
        "notes": "1 km JPEG derivative of the 12 GB 232 m master. Colourized Viking imagery, not multispectral true colour.",
    },
    "earth": {
        "fetch": "earth_color",
        "name": "NASA Blue Marble Next Generation, December 2004, with topography and bathymetry",
        "product": "Blue Marble: Next Generation 21600x10800 (2 km/pixel)",
        "url": "https://visibleearth.nasa.gov/images/73909/"
        "december-blue-marble-next-generation-w-topography-and-bathymetry",
        "file": "world.topo.bathy.200412.3x21600x10800.jpg",
        "license": "NASA content is generally not subject to US copyright; acknowledge NASA Earth Observatory.",
        "credits": "NASA Earth Observatory; produced by Reto Stöckli, NASA GSFC.",
        "projection": "equirectangular (plate carree), north up, WGS84, -180..180, prime meridian at image centre",
        "sha256": "3006C58B1272362DB0A8C2DF02DC07CEA4B12DFE820B7DC4A159A075CAF5D4D4",
        "notes": "Monthly MODIS composite with shaded topography and bathymetry; aesthetic true-colour base map, not a multispectral science product.",
    },
}


def _log(message: str) -> None:
    print(f"[imagery] {message}", flush=True)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def read_manifest(path: Path) -> dict:
    """Load and minimally validate the existing tile manifest."""
    if not path.is_file():
        raise FileNotFoundError(f"missing tile bake; run the normal bake first: {path}")
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest.get("format") != "spm-region-tiles":
        raise ValueError(f"unsupported manifest format: {manifest.get('format')!r}")
    if manifest.get("version") != config.TILE_FORMAT_VERSION:
        raise ValueError(f"unsupported manifest version: {manifest.get('version')!r}")
    return manifest


def validate_manifest(manifest: dict, body: BodyDef, grid: hexify.Grid, out_dir: Path) -> None:
    """Ensure the sidecar order will match the existing ``fine.bin``."""
    manifest_body = manifest.get("body", {}).get("key")
    if manifest_body != body.key:
        raise ValueError(f"manifest body {manifest_body!r} does not match --body={body.key!r}")
    if manifest.get("fineResolution") != grid.fine_resolution:
        raise ValueError("manifest fine resolution does not match the H3 grid")
    if manifest.get("regionResolution") != grid.region_resolution:
        raise ValueError("manifest region resolution does not match the H3 grid")

    regions = manifest.get("regions", [])
    if len(regions) != grid.region_ids.size:
        raise ValueError(f"manifest has {len(regions)} regions; grid has {grid.region_ids.size}")
    hex_count = sum(int(region.get("hexCount", 0)) for region in regions)
    if hex_count != grid.total:
        raise ValueError(f"manifest hex count {hex_count} does not match grid total {grid.total}")

    tile_path = out_dir / manifest.get("tilesFile", "fine.bin")
    expected_bytes = sum(int(region.get("length", 0)) for region in regions)
    if not tile_path.is_file():
        raise FileNotFoundError(f"missing tile file: {tile_path}")
    if tile_path.stat().st_size != expected_bytes:
        raise ValueError(f"{tile_path.name} size does not match its region index")


def align_rgb(rgb: np.ndarray) -> np.ndarray:
    """Roll a -180..180 north-up image onto the bake's 0..360 convention."""
    if rgb.ndim != 3 or rgb.shape[2] != 3:
        raise ValueError(f"expected an HxWx3 RGB image, got {rgb.shape}")
    return np.roll(rgb, -rgb.shape[1] // 2, axis=1)


def make_sampler(rgb: np.ndarray):
    """Return a nearest-pixel sampler over the aligned 0..360, north-up grid."""
    height, width = rgb.shape[:2]
    dlat = 180.0 / height
    dlon = 360.0 / width

    def sample(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
        row = np.floor((LAT_NORTH - np.asarray(lat)) / dlat).astype(np.int64)
        col = np.floor(np.mod(np.asarray(lon), 360.0) / dlon).astype(np.int64)
        np.clip(row, 0, height - 1, out=row)
        np.clip(col, 0, width - 1, out=col)
        return rgb[row, col]

    return sample


def load_source_rgb(path: Path, ppd: int) -> np.ndarray:
    """Decode a source image onto a manageable 0..360 north-up RGB grid."""
    width = ppd * 360
    height = ppd * 180
    # The pinned Mars source is ~228 MP.  Its SHA-256 is checked before this
    # call, so raise Pillow's generic decompression-bomb limit for this decode.
    old_limit = Image.MAX_IMAGE_PIXELS
    Image.MAX_IMAGE_PIXELS = None
    try:
        with Image.open(path) as image:
            # JPEG's draft mode decodes at a smaller scale before resizing,
            # which keeps the 21k x 10k source within a reasonable budget.
            if image.format == "JPEG":
                image.draft("RGB", (width, height))
            rgb = np.asarray(
                image.convert("RGB").resize((width, height), Image.Resampling.BILINEAR),
                dtype=np.uint8,
            )
    finally:
        Image.MAX_IMAGE_PIXELS = old_limit
    return align_rgb(rgb)


def aggregate_to_hex(
    grid: hexify.Grid,
    rgb: np.ndarray,
    *,
    jobs: int,
    block_rows: int,
) -> np.ndarray:
    """Average sampled RGB per fine hex; fill empty hexes at their centroids."""
    sample = make_sampler(rgb)
    sums = np.zeros((grid.total, BYTES_PER_HEX), dtype=np.float32)
    counts = np.zeros(grid.total, dtype=np.uint32)
    height = rgb.shape[0]
    dlat = 180.0 / height

    for start in range(0, height, block_rows):
        end = min(start + block_rows, height)
        # global_latlon() always starts at its lat_north argument and derives
        # row spacing from the height it is given.  A block is only a slice of
        # the source grid, so pass both the offset latitude and the full-grid
        # dlat; otherwise later rows are compressed onto northern hexes.
        lat, lon = raster.global_latlon(
            end - start,
            rgb.shape[1],
            lon_west=LON_WEST,
            lat_north=LAT_NORTH - start * dlat,
            dlat=dlat,
        )
        cells = hexify.pixels_to_hex(
            lat.ravel(), lon.ravel(), grid.fine_resolution, jobs=jobs
        )
        indices = grid.lookup(cells)
        block = rgb[start:end].reshape(-1, BYTES_PER_HEX).astype(np.float32)
        counts += np.bincount(indices, minlength=grid.total).astype(np.uint32)
        for channel in range(BYTES_PER_HEX):
            sums[:, channel] += np.bincount(
                indices, weights=block[:, channel], minlength=grid.total
            )

    empty = np.flatnonzero(counts == 0)
    if empty.size:
        _log(f"filling {empty.size} unsampled hexes at their centroids")
        for start in range(0, empty.size, 100_000):
            batch = empty[start : start + 100_000]
            lat = np.empty(batch.size, dtype=np.float64)
            lon = np.empty(batch.size, dtype=np.float64)
            for i, cell in enumerate(batch):
                lat[i], lon[i] = h3.cell_to_latlng(int(grid.hex_ids[cell]))
            sums[batch] = sample(lat, lon).astype(np.float32)
            counts[batch] = 1

    if np.any(counts == 0):  # pragma: no cover - defensive
        raise RuntimeError("some fine hexes still have no imagery sample")
    return np.rint(sums / counts[:, None]).astype(np.uint8)


def synthetic_rgb(body: BodyDef, grid: hexify.Grid) -> np.ndarray:
    """Deterministic body-coloured fixture for CI; no source image required."""
    out = np.empty((grid.total, BYTES_PER_HEX), dtype=np.uint8)

    # Earth shares the tile baker's per-hex surface field, so colour, biome and
    # elevation describe the same coastlines instead of three different blobs.
    if body.key == "earth":
        for start, end, lat, lon in synthetic_earth.latlon_batches(grid):
            out[start:end] = synthetic_earth.rgb(body, lat, lon)
        return out

    for r, region in enumerate(grid.region_ids):
        lat, lon = h3.cell_to_latlng(int(region))
        start, end = grid.region_slice(r)
        ordinal = np.arange(end - start, dtype=np.float32)
        noise = (ordinal * 37.0) % 101.0 - 50.0

        if body.key == "moon":
            grey = (
                138.0
                + 24.0 * np.sin(np.deg2rad(lat * 3.0))
                + 16.0 * np.cos(np.deg2rad(lon * 2.0))
                + 0.18 * noise
            )
            mare = (abs(lon) < 55.0) & (abs(lat) < 28.0)
            grey[mare] -= 42.0
            grey = np.clip(grey, 30.0, 225.0)
            out[start:end] = np.stack([grey, grey, grey], axis=1).astype(np.uint8)
            continue

        if body.key == "mars":
            red = 172.0 + 21.0 * np.sin(np.deg2rad(lat * 2.0)) + 0.16 * noise
            green = 96.0 + 14.0 * np.cos(np.deg2rad(lon * 3.0)) + 0.12 * noise
            blue = 62.0 + 9.0 * np.sin(np.deg2rad(lon * 2.0)) + 0.10 * noise
            canyon = (30.0 < lon < 100.0) and (-25.0 < lat < 10.0)
            polar = abs(lat) >= body.pole_latitude_deg
            if canyon:
                red -= 28.0
                green -= 18.0
            if polar:
                red += 38.0
                green += 42.0
                blue += 45.0
            out[start:end] = np.stack(
                [np.clip(red, 0.0, 255.0), np.clip(green, 0.0, 255.0), np.clip(blue, 0.0, 255.0)],
                axis=1,
            ).astype(np.uint8)
            continue

        # Stable fallback for any future body added to the pipeline.
        base = (ordinal * 13.0) % 180.0 + 40.0
        out[start:end] = np.stack([base, base * 0.7, base * 0.4], axis=1).astype(np.uint8)

    return out


def write_sidecar(
    rgb: np.ndarray,
    body: BodyDef,
    grid: hexify.Grid,
    out_dir: Path,
    *,
    source: dict,
    generated_utc: str,
) -> tuple[Path, Path]:
    """Write ``color.bin`` and ``imagery.json``; never touch the tile bake."""
    out_dir.mkdir(parents=True, exist_ok=True)
    color_bytes = np.ascontiguousarray(rgb, dtype=np.uint8).tobytes()
    color_path = out_dir / COLOR_FILE
    color_path.write_bytes(color_bytes)

    imagery = {
        "format": IMAGERY_FORMAT,
        "version": IMAGERY_VERSION,
        "generatedUtc": generated_utc,
        "body": {"key": body.key, "displayName": body.display_name},
        "fineResolution": grid.fine_resolution,
        "regionResolution": grid.region_resolution,
        "hexCount": grid.total,
        "colorFile": color_path.name,
        "bytesPerHex": BYTES_PER_HEX,
        "order": "fine.bin child order",
        "source": source,
        "colorSha256": hashlib.sha256(color_bytes).hexdigest().upper(),
    }
    imagery_path = out_dir / IMAGERY_FILE
    imagery_path.write_text(json.dumps(imagery, indent=2), encoding="utf-8")
    return color_path, imagery_path


def run(args: argparse.Namespace) -> tuple[Path, Path]:
    body = get_body(args.body)
    out_dir = Path(args.out) if args.out else config.DATA_DIR / body.key
    manifest = read_manifest(out_dir / "manifest.json")
    grid = hexify.enumerate_grid(
        int(manifest["regionResolution"]), int(manifest["fineResolution"])
    )
    validate_manifest(manifest, body, grid, out_dir)
    _log(f"{body.key}: {grid.region_ids.size} regions, {grid.total} fine hexes")

    if args.synthetic:
        _log("building deterministic synthetic colour fixture")
        rgb = synthetic_rgb(body, grid)
        source = {
            "name": f"Synthetic deterministic surface colour for {body.display_name}",
            "product": "CI fixture (not science data)",
            "url": None,
            "license": "Generated in-repository; no external imagery.",
            "credits": "Space Program Manager pipeline",
            "projection": "none; colours assigned directly per H3 region",
            "synthetic": True,
        }
        generated_utc = str(manifest.get("generatedUtc", "1970-01-01T00:00:00+00:00"))
    else:
        info = SOURCES.get(body.key)
        if info is None:  # pragma: no cover - guarded by known bodies today
            raise ValueError(f"no imagery source configured for {body.key!r}")
        _log(f"fetching {info['name']}")
        source_path = fetch.fetch(info["fetch"], body_key=body.key, force=args.force)
        actual_sha = _sha256(source_path)
        if actual_sha != info["sha256"]:
            raise ValueError(
                f"{source_path.name} SHA-256 {actual_sha} does not match expected {info['sha256']}"
            )
        _log(f"sampling imagery at {args.ppd} ppd")
        image = load_source_rgb(source_path, args.ppd)
        rgb = aggregate_to_hex(grid, image, jobs=args.jobs, block_rows=args.block_rows)
        source = {
            "name": info["name"],
            "product": info["product"],
            "url": info["url"],
            "file": info["file"],
            "license": info["license"],
            "credits": info["credits"],
            "projection": info["projection"],
            "alignment": "roll -width/2; no vertical flip",
            "sourceSha256": info["sha256"],
            "sampledPixelsPerDegree": args.ppd,
            "sampledWidth": image.shape[1],
            "sampledHeight": image.shape[0],
            "synthetic": False,
            "notes": info["notes"],
        }
        generated_utc = datetime.now(timezone.utc).isoformat(timespec="seconds")

    color_path, imagery_path = write_sidecar(
        rgb,
        body,
        grid,
        out_dir,
        source=source,
        generated_utc=generated_utc,
    )
    _log(f"wrote {color_path.name} ({color_path.stat().st_size} bytes) and {imagery_path.name}")
    return color_path, imagery_path


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="spm-bake-imagery", description=__doc__)
    parser.add_argument("--body", default="moon", help="body key (default: moon)")
    parser.add_argument("--out", default=None, help="output directory (default: data/<body>)")
    parser.add_argument(
        "--synthetic",
        action="store_true",
        help="write a deterministic in-memory colour fixture (no downloads)",
    )
    parser.add_argument("--force", action="store_true", help="re-download the source image")
    parser.add_argument(
        "--ppd",
        type=int,
        default=16,
        choices=[4, 16, 64],
        help="sampled pixels per degree for real imagery (default: 16)",
    )
    parser.add_argument("--jobs", type=int, default=1, help="parallel workers for pixel->hex")
    parser.add_argument(
        "--block-rows", type=int, default=512, help="raster rows per aggregation block"
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    t0 = time.time()
    run(args)
    _log(f"done in {time.time() - t0:.1f}s")
    return 0
