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

import numpy as np

from .bodies import BodyDef


def classify_surface(
    body: BodyDef,
    geology_dn: np.ndarray,
    elevation_m: np.ndarray,
    lat: np.ndarray,
) -> np.ndarray:
    """Return an ``int8`` raster of biome codes (indices into ``body.biomes``)."""
    if not (geology_dn.shape == elevation_m.shape == lat.shape):
        raise ValueError(
            "geology_dn, elevation_m and lat must share a shape, got "
            f"{geology_dn.shape}, {elevation_m.shape}, {lat.shape}"
        )

    code = {
        b.id: i for i, b in enumerate(body.biomes)
    }

    mare = np.isin(geology_dn, np.fromiter(body.mare_geology_dns, dtype=np.int8))

    # Elevation band index: 0 = Lowlands, 1 = Midlands, 2 = Highlands.
    band = np.zeros(elevation_m.shape, dtype=np.uint8)
    band[elevation_m >= body.lowlands_max_m] = 1
    band[elevation_m >= body.midlands_max_m] = 2

    north = lat >= body.pole_latitude_deg
    south = lat <= -body.pole_latitude_deg

    band_codes = np.array(
        [
            [code["luna.pole_south_lowlands"], code["luna.pole_south_midlands"], code["luna.pole_south_highlands"]],
            [code["luna.lowlands"], code["luna.midlands"], code["luna.highlands"]],
            [code["luna.pole_north_lowlands"], code["luna.pole_north_midlands"], code["luna.pole_north_highlands"]],
        ],
        dtype=np.int8,
    )

    # row 0 = south, row 1 = equatorial, row 2 = north
    hemisphere = np.ones(elevation_m.shape, dtype=np.uint8)
    hemisphere[south] = 0
    hemisphere[north] = 2

    out = band_codes[hemisphere, band]
    out[mare] = code["luna.mare"]
    return out.astype(np.int8, copy=False)
