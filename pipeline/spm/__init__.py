"""Space Program Manager offline data pipeline.

Bakes real planetary source data into engine-ready region tiles.

The pipeline is split into the stages described in the development plan
(§3), implemented as modules rather than sub-packages:

    fetch.py      download + cache source datasets
    raster.py     raster / PDS image I/O and geographic grids
    classify.py   raster -> per-pixel biome classification
    crater.py     Robbins crater catalogue -> per-hex crater overlay
    hexify.py     H3 aggregation, dominant-biome + tie-break
    export.py     write region tiles + body manifest
"""

__all__ = [
    "config",
    "bodies",
    "fetch",
    "raster",
    "classify",
    "crater",
    "hexify",
    "export",
]
