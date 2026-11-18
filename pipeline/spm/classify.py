"""Per-pixel biome classification from geology + elevation + latitude.

Chain (design doc §2.2, applied per raster pixel before H3 aggregation):

1. **Mare** - geology DN is a mare unit.  Mare is a distinct basalt material,
   so it wins over the elevation bands.
2. **Pole tiers** - ``|lat| >= pole_latitude_deg`` selects the north/south
   pole biome names.
3. **Elevation bands** - Lowlands / Midlands / Highlands from the DEM.

The dedicated ``Craters`` biome is *not* assigned here; it is layered on top
from the Robbins catalogue in :mod:`spm.crater`.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np

from .bodies import BodyDef

#: Mars geology-derived province codes.  Higher values are drawn last when
#: rasterizing so polar deposits win over the underlying unit polygon.
MARS_GEO_NONE = 0
MARS_GEO_CANYON = 1
MARS_GEO_VOLCANIC = 2
MARS_GEO_POLAR = 3


def classify_surface(
    body: BodyDef,
    geology_dn: np.ndarray | None,
    elevation_m: np.ndarray,
    lat: np.ndarray,
) -> np.ndarray:
    """Return an ``int8`` raster of biome codes (indices into ``body.biomes``).

    ``geology_dn`` may be ``None`` for bodies without a configured geology
    raster (Mars today); those bodies are classified from elevation and
    latitude alone.  When it is present it must share the DEM grid shape.
    """
    if geology_dn is not None and not (geology_dn.shape == elevation_m.shape == lat.shape):
        raise ValueError(
            "geology_dn, elevation_m and lat must share a shape, got "
            f"{geology_dn.shape}, {elevation_m.shape}, {lat.shape}"
        )

    code = {b.id: i for i, b in enumerate(body.biomes)}

    if geology_dn is not None and body.mare_biome_id and body.mare_geology_dns:
        mare = np.isin(geology_dn, np.fromiter(body.mare_geology_dns, dtype=np.int8))
    else:
        mare = np.zeros(elevation_m.shape, dtype=bool)

    # Elevation band index: 0 = Lowlands, 1 = Midlands, 2 = Highlands.
    band = np.zeros(elevation_m.shape, dtype=np.uint8)
    band[elevation_m >= body.lowlands_max_m] = 1
    band[elevation_m >= body.midlands_max_m] = 2

    north = lat >= body.pole_latitude_deg
    south = lat <= -body.pole_latitude_deg

    band_codes = np.array(
        [
            [code[biome_id] for biome_id in body.pole_south_biome_ids],
            [
                code[body.lowlands_biome_id],
                code[body.midlands_biome_id],
                code[body.highlands_biome_id],
            ],
            [code[biome_id] for biome_id in body.pole_north_biome_ids],
        ],
        dtype=np.int8,
    )

    # row 0 = south, row 1 = equatorial, row 2 = north
    hemisphere = np.ones(elevation_m.shape, dtype=np.uint8)
    hemisphere[south] = 0
    hemisphere[north] = 2

    out = band_codes[hemisphere, band]
    if body.mare_biome_id is not None:
        out[mare] = code[body.mare_biome_id]
    return out.astype(np.int8, copy=False)


def _mars_geo_class(unit_name: object, unit_symbol: object) -> int:
    """Map one USGS Mars geologic unit to a province code."""
    name = str(unit_name or "").lower()
    symbol = str(unit_symbol or "").strip().lower()

    if "polar ice" in name or "polar layered" in name:
        return MARS_GEO_POLAR

    volcanic = (
        "patera",
        "tholus",
        "olympus mons",
        "tharsis montes",
        "elysium formation",
        "alba patera",
        "volcano",
        "dome",
    )
    if any(term in name for term in volcanic) or symbol in {"v", "d"}:
        return MARS_GEO_VOLCANIC

    canyon = (
        "valles marineris",
        "chaotic",
        "channel",
        "flood-plain",
        "flood plain",
        "canyon",
        "chasm",
    )
    if any(term in name for term in canyon):
        return MARS_GEO_CANYON

    return MARS_GEO_NONE


def read_mars_geology(
    shapefile: str | Path,
    out_shape: tuple[int, int],
) -> np.ndarray:
    """Rasterize the USGS Mars 15M geology polygons onto the MOLA grid.

    Returns a ``uint8`` province raster aligned with the DEM: 0 unclassified,
    1 canyon, 2 volcanic, 3 polar.  The source polygons are coarse enough that
    rasterizing at 1 degree and nearest-upsampling matches a full-resolution
    rasterize while avoiding millions of polygon/pixel intersection tests.
    """
    import geopandas as gpd
    from rasterio.features import rasterize
    from rasterio.transform import from_origin

    coarse_shape = (180, 360)
    scale_y, scale_x = out_shape[0] // 180, out_shape[1] // 360
    if scale_y < 1 or scale_x < 1 or out_shape != (180 * scale_y, 360 * scale_x):
        raise ValueError(f"MOLA grid {out_shape} is not an integer multiple of 1 degree")

    gdf = gpd.read_file(shapefile)
    shapes = sorted(
        (
            (geometry, _mars_geo_class(name, symbol))
            for geometry, name, symbol in zip(
                gdf.geometry, gdf["UnitName"], gdf["UnitSymbol"]
            )
        ),
        key=lambda item: item[1],
    )
    coarse = rasterize(
        shapes,
        out_shape=coarse_shape,
        transform=from_origin(-180.0, 90.0, 1.0, 1.0),
        fill=MARS_GEO_NONE,
        dtype=np.uint8,
        all_touched=False,
    )
    # The MOLA grid starts at 0E; the geologic polygons are -180..180.
    # Roll the prime meridian to column 0 before upsampling to the DEM grid.
    aligned = np.roll(coarse, -180, axis=1)
    return np.repeat(np.repeat(aligned, scale_y, axis=0), scale_x, axis=1)


def classify_mars_surface(
    body: BodyDef,
    geology_class: np.ndarray,
    elevation_m: np.ndarray,
    lat: np.ndarray,
) -> np.ndarray:
    """Return Mars biome codes from elevation, latitude and geology provinces.

    Elevation/latitude establish the lowland/midland/highland or polar band;
    the USGS geology raster then overrides canyon and volcanic provinces.
    """
    if not (geology_class.shape == elevation_m.shape == lat.shape):
        raise ValueError(
            "geology_class, elevation_m and lat must share a shape, got "
            f"{geology_class.shape}, {elevation_m.shape}, {lat.shape}"
        )

    code = {b.id: i for i, b in enumerate(body.biomes)}
    band = np.zeros(elevation_m.shape, dtype=np.uint8)
    band[elevation_m >= body.lowlands_max_m] = 1
    band[elevation_m >= body.midlands_max_m] = 2

    band_codes = np.array(
        [
            [code[biome_id] for biome_id in body.pole_south_biome_ids],
            [
                code[body.lowlands_biome_id],
                code[body.midlands_biome_id],
                code[body.highlands_biome_id],
            ],
            [code[biome_id] for biome_id in body.pole_north_biome_ids],
        ],
        dtype=np.int8,
    )
    hemisphere = np.ones(elevation_m.shape, dtype=np.uint8)
    hemisphere[lat <= -body.pole_latitude_deg] = 0
    hemisphere[lat >= body.pole_latitude_deg] = 2

    out = band_codes[hemisphere, band].astype(np.int8, copy=True)

    if body.canyon_biome_id is not None:
        out[geology_class == MARS_GEO_CANYON] = code[body.canyon_biome_id]
    if body.volcanic_biome_id is not None:
        out[geology_class == MARS_GEO_VOLCANIC] = code[body.volcanic_biome_id]

    polar = geology_class == MARS_GEO_POLAR
    out[polar & (lat >= 0.0)] = code[body.pole_north_biome_ids[0]]
    out[polar & (lat < 0.0)] = code[body.pole_south_biome_ids[0]]
    return out
