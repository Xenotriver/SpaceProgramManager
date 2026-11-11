"""Raster I/O and geographic grid helpers.

Two source layouts are used for the Moon:

* **LOLA GDR DEM** - PDS3 ``IMG``: a bare little-endian ``int16`` array
  (no header) described by a sibling ``LBL``.  Simple-cylindrical,
  longitude ``0..360`` (east positive), pixel registered.
* **USGS Unified Geologic Map raster** - a GeoTIFF, single ``int8`` band of
  geologic-unit DN codes.  Simple-cylindrical, longitude ``-180..180``.
"""

from __future__ import annotations

import re
from pathlib import Path

import numpy as np

# ---------------------------------------------------------------------------
# LOLA PDS3 IMG
# ---------------------------------------------------------------------------

def parse_lola_label(label_path: str | Path) -> dict[str, float]:
    """Extract the handful of fields we need from a LOLA ``.LBL``."""
    text = Path(label_path).read_text(errors="replace")
    out: dict[str, float] = {}
    for key in (
        "LINES",
        "LINE_SAMPLES",
        "SAMPLE_BITS",
        "SCALING_FACTOR",
        "OFFSET",
        "MAP_RESOLUTION",
    ):
        m = re.search(rf"^\s*{key}\s*=\s*(-?[\d.]+)", text, re.MULTILINE)
        if m:
            out[key] = float(m.group(1))
    return out


def read_lola_img(
    img_path: str | Path,
    label_path: str | Path | None = None,
    *,
    lines: int | None = None,
    samples: int | None = None,
    scaling_factor: float = 0.5,
) -> np.ndarray:
    """Read a LOLA GDR ``IMG`` into an elevation array (metres, float32).

    ``elevation = DN * SCALING_FACTOR`` relative to the reference radius.
    """
    img_path = Path(img_path)
    if label_path is not None:
        meta = parse_lola_label(label_path)
        lines = int(meta.get("LINES", lines or 0)) or lines
        samples = int(meta.get("LINE_SAMPLES", samples or 0)) or samples
        scaling_factor = meta.get("SCALING_FACTOR", scaling_factor)

    if not lines or not samples:
        raise ValueError("lines/samples are required when no label is supplied")

    dn = np.fromfile(img_path, dtype="<i2")
    expected = lines * samples
    if dn.size != expected:
        raise ValueError(
            f"{img_path.name}: expected {expected} int16 samples, found {dn.size}"
        )
    dn = dn.reshape(lines, samples)
    return dn.astype(np.float32) * float(scaling_factor)


# ---------------------------------------------------------------------------
# GeoTIFF (geology)
# ---------------------------------------------------------------------------


def read_geology(
    tif_path: str | Path,
    *,
    out_shape: tuple[int, int] | None = None,
    lon_west_in: float = -180.0,
    lon_west_out: float | None = None,
) -> np.ndarray:
    """Read the UGM geologic-unit raster as ``int8`` DN codes.

    ``out_shape`` decimates on read (nearest neighbour) so a 64 ppd source
    can be consumed directly at 16 ppd without materialising the full grid.

    The UGM GeoTIFF is simple-cylindrical ``-180..180`` while the LOLA DEM is
    ``0..360``.  When ``lon_west_out`` differs from ``lon_west_in`` the raster
    is rolled in longitude so the two grids line up (a naive index-by-index
    resize would otherwise shift the geology by 180 degrees).
    """
    import rasterio
    from rasterio.enums import Resampling

    with rasterio.open(tif_path) as ds:
        if out_shape is not None and out_shape != (ds.height, ds.width):
            data = ds.read(1, out_shape=out_shape, resampling=Resampling.nearest)
        else:
            data = ds.read(1)

    if lon_west_out is not None and lon_west_out != lon_west_in:
        width = data.shape[1]
        shift = int(round(((lon_west_out - lon_west_in) % 360.0) / 360.0 * width)) % width
        if shift:
            data = np.roll(data, -shift, axis=1)

    return data.astype(np.int8, copy=False)


# ---------------------------------------------------------------------------
# Geographic grids
# ---------------------------------------------------------------------------


def global_latlon(
    height: int,
    width: int,
    *,
    lon_west: float,
    lat_north: float,
) -> tuple[np.ndarray, np.ndarray]:
    """Pixel-centre latitude/longitude arrays for a global equirectangular grid.

    Returns ``(lat, lon)`` as 2-D arrays of shape ``(height, width)`` where
    ``lat`` varies down the rows and ``lon`` across the columns.
    """
    dlat = 180.0 / height
    dlon = 360.0 / width
    lat = lat_north - (np.arange(height, dtype=np.float64) + 0.5) * dlat
    lon = lon_west + (np.arange(width, dtype=np.float64) + 0.5) * dlon
    lat2d = np.repeat(lat[:, None], width, axis=1)
    lon2d = np.repeat(lon[None, :], height, axis=0)
    return lat2d, lon2d


def resample_nearest(src: np.ndarray, out_h: int, out_w: int) -> np.ndarray:
    """Nearest-neighbour resample to ``(out_h, out_w)`` (block-sampled)."""
    in_h, in_w = src.shape
    if (in_h, in_w) == (out_h, out_w):
        return src
    iy = np.minimum((np.arange(out_h) * (in_h / out_h)).astype(np.int64), in_h - 1)
    ix = np.minimum((np.arange(out_w) * (in_w / out_w)).astype(np.int64), in_w - 1)
    return src[np.ix_(iy, ix)]


def lon_0_360_to_pm180(lon: np.ndarray) -> np.ndarray:
    """Map longitudes from ``0..360`` to ``-180..180``."""
    return ((lon + 180.0) % 360.0) - 180.0
