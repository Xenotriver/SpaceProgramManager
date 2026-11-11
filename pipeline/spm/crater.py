"""Robbins (2019) lunar crater catalogue -> per-hex crater overlay.

The overlay is an H3 per-hex attribute (design doc §2.3), **not** an elevation
rank and **not** a biome:

* **single-cell crater** - the crater fits inside one fine hex
  (``D <= hex diameter``): a 30% speed penalty.
* **multi-cell crater** - the crater spans several hexes: a 20% speed penalty.

The Moon is saturated with craters: the *floors* of all craters are almost
everywhere (craters >= 1 km geometrically cover ~98% of the surface).  Marking
whole floors would put a penalty on nearly every hex, so a multi-cell crater
marks only a **rim band** - the ring of hexes at the crater's rim radius.  The
interior (floor) stays penalty-free, so basins remain drivable and only the
rough rim has to be crossed.

Where a hex is covered by both classes the *multi-cell* class wins (a large
crater's rim dominates a small crater's floor).
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

from h3.api import basic_int as h3

from .bodies import BodyDef

#: Mean Earth radius used by H3's edge-length tables.
EARTH_RADIUS_KM = 6371.0072

CRATER_NONE = 0
CRATER_SINGLE = 1
CRATER_MULTI = 2


@dataclass
class CraterOverlay:
    """Per-hex crater data, keyed by fine-hex integer index."""

    hexes: np.ndarray  # int64
    kinds: np.ndarray  # uint8


def hex_edge_km(body: BodyDef, resolution: int) -> float:
    """Average H3 hexagon edge length on ``body`` at ``resolution``."""
    import h3 as _h3

    earth_edge = _h3.average_hexagon_edge_length(resolution, unit="km")
    return float(earth_edge) * (body.radius_km / EARTH_RADIUS_KM)


def load_robbins(csv_path: str | Path, *, min_diameter_km: float) -> pd.DataFrame:
    """Read the Robbins catalogue, keeping only lat/lon/diameter columns."""
    df = pd.read_csv(
        csv_path,
        usecols=["LAT_CIRC_IMG", "LON_CIRC_IMG", "DIAM_CIRC_IMG"],
    )
    df = df.rename(
        columns={
            "LAT_CIRC_IMG": "lat",
            "LON_CIRC_IMG": "lon",
            "DIAM_CIRC_IMG": "diameter_km",
        }
    )
    df = df[df["diameter_km"] >= min_diameter_km]
    # Catalogue longitudes run 0..360; H3 wants -180..180.
    df["lon"] = ((df["lon"] + 180.0) % 360.0) - 180.0
    return df.reset_index(drop=True)


def build_overlay(
    body: BodyDef,
    craters: pd.DataFrame,
    *,
    resolution: int | None = None,
    rim_rings: int = 0,
) -> CraterOverlay:
    """Build the crater overlay for ``body`` at its fine resolution.

    ``rim_rings`` widens the multi-cell rim band to ``2 * rim_rings + 1``
    hex rings centred on the crater's rim radius (``0`` = a single ring).
    """
    resolution = resolution or body.fine_resolution
    edge = hex_edge_km(body, resolution)
    diameter = 2.0 * edge
    spacing = math.sqrt(3.0) * edge

    lat = craters["lat"].to_numpy(dtype=np.float64)
    lon = craters["lon"].to_numpy(dtype=np.float64)
    diam = craters["diameter_km"].to_numpy(dtype=np.float64)

    overlay: dict[int, int] = {}

    for i in range(len(craters)):
        centre = h3.latlng_to_cell(float(lat[i]), float(lon[i]), resolution)
        d = float(diam[i])

        if d <= diameter:
            # Fits within a single hex: the whole hex is crater terrain.
            if overlay.get(centre, CRATER_NONE) < CRATER_MULTI:
                overlay[centre] = CRATER_SINGLE
            continue

        # Rim band: the ring(s) of hexes at the crater's rim radius.
        k = max(1, int(round((d / 2.0) / spacing)))
        for ring in range(max(1, k - rim_rings), k + rim_rings + 1):
            for cell in h3.grid_ring(centre, ring):
                overlay[cell] = CRATER_MULTI

    hexes = np.fromiter(overlay.keys(), dtype=np.int64, count=len(overlay))
    kinds = np.fromiter(overlay.values(), dtype=np.uint8, count=len(overlay))
    return CraterOverlay(hexes=hexes, kinds=kinds)
