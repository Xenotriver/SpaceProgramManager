"""Real Earth inputs for the tile bake.

The synthetic fixture in :mod:`spm.synthetic_earth` remains the deterministic CI
path.  This module is the real-data path: public-domain coastline, lake, river
and volcano vectors from Natural Earth, NOAA ETOPO relief for elevation and
bathymetry, and NASA Blue Marble colour for the land-cover proxy.  All sources
are pinned and cached by :mod:`spm.fetch`; nothing here is written to ``data/``
except through the normal tile/imagery bakers.

Sampling is done at every res-6 hex centroid in bounded region batches, exactly
as for the synthetic Earth fixture, so biome, elevation and imagery describe the
same coastlines.
"""

from __future__ import annotations

import gzip
import shutil
import zipfile
from pathlib import Path

import numpy as np

from . import fetch, imagery
from .bodies import BodyDef

#: Resolution of the rasterised coastline/lake/river masks (pixels per degree).
LAND_PPD = 60
#: Resolution of the elevation and colour rasters, matching the other bodies.
RASTER_PPD = 16

FETCH_KEYS = (
    "earth_land",
    "earth_lakes",
    "earth_rivers",
    "earth_elevation",
    "earth_color",
)

SOURCE_LABELS = {
    "dem": "NOAA ETOPO1 1-arcminute Ice surface grid, grid registered "
    "(public domain; Amante & Eakins 2009)",
    "geology": "Natural Earth 1:10m land, lakes and river centrelines "
    "(public domain)",
    "imagery": "NASA Blue Marble Next Generation, December 2004 with topography "
    "and bathymetry (public domain; NASA Earth Observatory)",
}


def _log(message: str) -> None:
    print(f"[earth] {message}", flush=True)


def ensure_inputs(*, force: bool = False) -> dict[str, Path]:
    """Download and unpack every real Earth input into the Earth cache.

    Returned keys are the short names used by :func:`sample`: ``land``,
    ``lakes``, ``rivers``, ``elevation`` and ``color``.
    """
    paths = {
        key.removeprefix("earth_"): fetch.fetch(key, body_key="earth", force=force)
        for key in FETCH_KEYS
    }
    for key in ("land", "lakes", "rivers"):
        paths[key] = _extract_shapefile(paths[key], prefix=key)
    elevation = paths["elevation"]
    if elevation.suffix == ".gz":
        unpacked = elevation.with_suffix("")
        if force or not unpacked.is_file():
            _log(f"unpacking {elevation.name}")
            with gzip.open(elevation, "rb") as src, unpacked.open("wb") as dst:
                shutil.copyfileobj(src, dst)
        paths["elevation"] = unpacked
    return paths


def _extract_shapefile(archive: Path, *, prefix: str) -> Path:
    """Extract a Natural Earth shapefile set into the cache as ``<prefix>.<ext>``."""
    out_dir = archive.parent
    with zipfile.ZipFile(archive) as zf:
        shapes = [name for name in zf.namelist() if name.lower().endswith(".shp")]
        if not shapes:
            raise ValueError(f"{archive.name}: no shapefile in archive")
        stem = Path(shapes[0]).stem
        wanted = [
            name
            for name in zf.namelist()
            if Path(name).stem == stem
            and Path(name).suffix.lower() in {".shp", ".shx", ".dbf", ".prj", ".cpg"}
        ]
        for name in wanted:
            target = out_dir / f"{prefix}{Path(name).suffix.lower()}"
            with zf.open(name) as src, target.open("wb") as dst:
                shutil.copyfileobj(src, dst)
    result = out_dir / f"{prefix}.shp"
    if not result.is_file():
        raise FileNotFoundError(f"{archive.name}: could not extract {result.name}")
    return result


def read_mask(
    path: Path,
    *,
    ppd: int = LAND_PPD,
    all_touched: bool = False,
) -> np.ndarray:
    """Rasterise a Natural Earth vector layer onto a 0..360, north-up mask."""
    import geopandas as gpd
    from rasterio.features import rasterize
    from rasterio.transform import from_origin

    frame = gpd.read_file(path)
    if frame.empty:
        return np.zeros((ppd * 180, ppd * 360), dtype=np.uint8)
    if frame.crs is not None and not frame.crs.is_geographic:
        frame = frame.to_crs("EPSG:4326")

    height, width = ppd * 180, ppd * 360
    transform = from_origin(-180.0, 90.0, 1.0 / ppd, 1.0 / ppd)
    values = rasterize(
        ((geometry, 1) for geometry in frame.geometry),
        out_shape=(height, width),
        transform=transform,
        fill=0,
        all_touched=all_touched,
        dtype=np.uint8,
    )
    # -180..180 -> 0..360, matching imagery.align_rgb() and the bake samplers.
    return np.roll(values, -width // 2, axis=1)


def read_elevation(path: Path, *, ppd: int = RASTER_PPD) -> np.ndarray:
    """Read the grid-registered ETOPO1 relief onto a 0..360 north-up grid.

    The GMT-4 ``.grd`` is a classic netCDF with an ``int32`` ``z(y, x)``
    variable whose ``y`` runs south-to-north and whose ``x`` runs -180..180
    with a duplicated seam column.  ``scipy.io.netcdf_file`` reads it directly
    (mmap-backed), avoiding a 934 MB in-memory copy and GDAL's historic
    row-order ambiguity for these grids.
    """
    from scipy.io import netcdf_file

    height, width = ppd * 180, ppd * 360
    source = netcdf_file(str(path), "r", mmap=True)
    data = grid = None
    try:
        grid = source.variables["z"]
        fill = int(getattr(grid, "_FillValue", -2147483648))
        data = grid.data
        rows = np.linspace(0, data.shape[0] - 1, height, dtype=np.int64)
        cols = np.linspace(0, data.shape[1] - 1, width, dtype=np.int64)
        # Reverse the south-to-north rows to get a north-up image.  Copy the
        # sampled cells so the mmap can be closed cleanly (no scipy warning).
        elevation = np.asarray(data[np.ix_(rows[::-1], cols)], dtype=np.float32).copy()
    finally:
        del data
        del grid
        source.close()
    elevation[elevation == fill] = -4000.0
    return np.ascontiguousarray(np.roll(elevation, -width // 2, axis=1))


def _indices(
    lat: np.ndarray,
    lon: np.ndarray,
    ppd: int,
) -> tuple[np.ndarray, np.ndarray]:
    step = 1.0 / ppd
    rows = np.floor((90.0 - np.asarray(lat)) / step).astype(np.int64)
    cols = np.floor(np.mod(np.asarray(lon), 360.0) / step).astype(np.int64)
    np.clip(rows, 0, ppd * 180 - 1, out=rows)
    np.clip(cols, 0, ppd * 360 - 1, out=cols)
    return rows, cols


def _shore_mask(land: np.ndarray, ocean: np.ndarray, water: np.ndarray) -> np.ndarray:
    """Land hexes within two land-mask pixels of the ocean."""
    from scipy.ndimage import binary_dilation

    padded = np.pad(ocean, ((0, 0), (2, 2)), mode="wrap")
    near_ocean = binary_dilation(padded, iterations=2)[:, 2:-2]
    return (land & near_ocean & ~water).astype(bool)


def sample(
    body: BodyDef,
    grid,
    paths: dict[str, Path],
    *,
    land_ppd: int = LAND_PPD,
    raster_ppd: int = RASTER_PPD,
    batch_regions: int = 64,
) -> tuple[np.ndarray, np.ndarray]:
    """Sample real geography at every fine-hex centroid.

    Returns ``(biome codes, elevation metres)`` in ``fine.bin`` child order.
    """
    from .synthetic_earth import latlon_batches

    _log(f"rasterising Natural Earth land/lakes/rivers at {land_ppd} ppd")
    land = read_mask(paths["land"], ppd=land_ppd).astype(bool)
    lakes = read_mask(paths["lakes"], ppd=land_ppd, all_touched=True).astype(bool)
    rivers = read_mask(paths["rivers"], ppd=land_ppd, all_touched=True).astype(bool)
    shore = _shore_mask(land, ~land, lakes | rivers)

    _log(f"reading ETOPO elevation at {raster_ppd} ppd")
    elevation_grid = read_elevation(paths["elevation"], ppd=raster_ppd)
    _log(f"reading Blue Marble land-cover proxy at {raster_ppd} ppd")
    color_grid = imagery.load_source_rgb(paths["color"], raster_ppd)

    ocean_code = body.biome_code("earth.ocean")
    codes = np.empty(grid.total, dtype=np.int8)
    elevation = np.empty(grid.total, dtype=np.float32)

    for start, end, lat, lon in latlon_batches(grid, batch_regions=batch_regions):
        lr, lc = _indices(lat, lon, land_ppd)
        rr, rc = _indices(lat, lon, raster_ppd)

        is_land = land[lr, lc]
        is_lake = lakes[lr, lc] & is_land
        is_river = rivers[lr, lc] & is_land & ~is_lake
        is_shore = shore[lr, lc]
        height = elevation_grid[rr, rc]
        rgb = color_grid[rr, rc].astype(np.float32)
        green = rgb[:, 1] - 0.5 * (rgb[:, 0] + rgb[:, 2])

        code = np.full(lat.shape, ocean_code, dtype=np.int8)
        ground = is_land & ~is_lake & ~is_river & ~is_shore
        mountains = ground & (height > 950.0)
        tundra = ground & ((lat >= 58.0) | (lat <= -55.0) | (height > 2600.0))
        taiga = ground & (lat >= 48.0) & ~tundra
        forest = ground & ~mountains & ~tundra & ~taiga & (green > 14.0)
        hills = ground & ~mountains & ~tundra & ~taiga & ~forest & (height > 420.0)

        def put(biome_id: str, mask: np.ndarray) -> None:
            if biome_id in body.biome_ids and np.any(mask):
                code[mask] = body.biome_code(biome_id)

        put("earth.plains", ground & ~mountains & ~tundra & ~taiga & ~forest & ~hills)
        put("earth.hills", hills)
        put("earth.forest", forest)
        put("earth.taiga", taiga)
        put("earth.tundra", tundra)
        put("earth.mountains", mountains)
        put("earth.shore", is_shore)
        put("earth.lake", is_lake)
        put("earth.river", is_river)
        code[lat >= body.pole_latitude_deg] = body.biome_code(
            body.pole_north_biome_ids[1]
        )
        code[lat <= -body.pole_latitude_deg] = body.biome_code(
            body.pole_south_biome_ids[1]
        )

        # Keep inland water flat for the renderers, as the synthetic bake does.
        height[is_lake | is_river] = np.minimum(height[is_lake | is_river], -5.0)
        codes[start:end] = code
        elevation[start:end] = height.astype(np.float32)

    return codes, elevation
