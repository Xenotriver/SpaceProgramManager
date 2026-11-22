"""Unit tests for the pinned real-Earth sampler.

These tests never download or read the large source rasters.  They exercise the
geographic conventions and the per-hex classification with small in-memory
stand-ins, so the real bake can be verified without network access.
"""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import numpy as np

PIPELINE_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PIPELINE_DIR))

from spm import earth_real, fetch, hexify, synthetic_earth  # noqa: E402
from spm.bodies import EARTH  # noqa: E402


def tiny_grid() -> hexify.Grid:
    offsets = np.array([0, 2, 4], dtype=np.int64)
    hex_ids = np.arange(4, dtype=np.int64)
    return hexify.Grid(
        region_resolution=3,
        fine_resolution=6,
        region_ids=np.array([1, 2], dtype=np.int64),
        hex_ids=hex_ids,
        offsets=offsets,
        sorted_hex=hex_ids.copy(),
        order=np.arange(4, dtype=np.int32),
    )


class EarthRealTests(unittest.TestCase):
    def test_indices_use_zero_to_360_north_up_convention(self) -> None:
        lat = np.array([90.0, 0.0, -90.0])
        lon = np.array([0.0, 180.0, 359.9])
        rows, cols = earth_real._indices(lat, lon, 4)
        np.testing.assert_array_equal(rows, [0, 360, 719])
        np.testing.assert_array_equal(cols, [0, 720, 1439])

    def test_every_real_earth_source_is_pinned(self) -> None:
        import hashlib

        for key in earth_real.FETCH_KEYS:
            source = fetch.SOURCES[key]
            self.assertIsNotNone(source.sha256, f"{key} has no SHA-256 pin")
            self.assertIsNotNone(source.size, f"{key} has no size pin")
            cached = fetch.cache_dir("earth") / source.filename
            if cached.is_file():
                digest = hashlib.sha256(cached.read_bytes()).hexdigest().upper()
                self.assertEqual(digest, source.sha256.upper(), f"{key} cache mismatch")

    def test_read_mask_rolls_minus_180_180_onto_zero_360(self) -> None:
        import geopandas as gpd
        from shapely.geometry import box

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "land.geojson"
            gpd.GeoDataFrame(
                {"id": [1]}, geometry=[box(-20.0, -10.0, 0.0, 10.0)], crs="EPSG:4326"
            ).to_file(path, driver="GeoJSON")
            mask = earth_real.read_mask(path, ppd=2)

        self.assertEqual(mask.shape, (360, 720))
        # The -20..0 box lands at columns 680..719 after the roll (340..360E).
        self.assertEqual(int(mask[170, 690]), 1)
        self.assertEqual(int(mask[170, 100]), 0)

    def test_sample_classifies_real_land_and_water(self) -> None:
        grid = tiny_grid()
        land = np.zeros((360, 720), dtype=bool)
        land[160:180, 330:370] = True  # near 5N, 170E after the roll
        lakes = np.zeros_like(land)
        rivers = np.zeros_like(land)
        elevation = np.full((360, 720), -4000.0, dtype=np.float32)
        elevation[160:180, 330:370] = 200.0
        colour = np.zeros((360, 720, 3), dtype=np.uint8)
        colour[..., 0] = 210  # ocean blue
        colour[..., 2] = 230
        colour[160:180, 330:370] = [110, 110, 90]  # neutral lowland

        batches = [
            (0, 2, np.array([5.0, 5.0]), np.array([170.0, 171.0])),
            (2, 4, np.array([0.0, 0.0]), np.array([10.0, 20.0])),
        ]
        with (
            mock.patch.object(
                earth_real, "read_mask", side_effect=[land, lakes, rivers]
            ),
            mock.patch.object(earth_real, "read_elevation", return_value=elevation),
            mock.patch.object(earth_real.imagery, "load_source_rgb", return_value=colour),
            mock.patch.object(synthetic_earth, "latlon_batches", return_value=batches),
        ):
            paths = {key.removeprefix("earth_"): Path(key) for key in earth_real.FETCH_KEYS}
            codes, heights = earth_real.sample(EARTH, grid, paths, land_ppd=2, raster_ppd=2)

        names = [EARTH.biomes[int(code)].id for code in codes]
        self.assertEqual(names[:2], ["earth.plains", "earth.plains"])
        self.assertEqual(names[2:], ["earth.ocean", "earth.ocean"])
        np.testing.assert_allclose(heights[:2], [200.0, 200.0])
        np.testing.assert_allclose(heights[2:], [-4000.0, -4000.0])


if __name__ == "__main__":
    unittest.main()
