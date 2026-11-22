"""Unit tests for the deterministic synthetic Earth surface field."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

import numpy as np

PIPELINE_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PIPELINE_DIR))

from spm import hexify, synthetic_earth  # noqa: E402
from spm.bodies import EARTH  # noqa: E402


def biome_ids(codes: np.ndarray) -> set[str]:
    return {EARTH.biomes[int(code)].id for code in np.unique(codes)}


class SyntheticEarthTests(unittest.TestCase):
    def test_field_is_deterministic(self) -> None:
        lat = np.array([45.0, 2.0, -25.0, -41.0, 70.0])
        lon = np.array([-100.0, 20.0, 134.0, 173.0, 0.0])

        first_codes, first_elevation = synthetic_earth.fields(EARTH, lat, lon)
        second_codes, second_elevation = synthetic_earth.fields(EARTH, lat, lon)

        np.testing.assert_array_equal(first_codes, second_codes)
        np.testing.assert_array_equal(first_elevation, second_elevation)

    def test_land_cover_has_a_plausible_extent(self) -> None:
        lat = np.arange(80.0, -82.0, -2.0)
        lon = np.arange(-180.0, 180.0, 2.0)
        mesh_lat, mesh_lon = np.meshgrid(lat, lon, indexing="ij")
        codes, _ = synthetic_earth.fields(EARTH, mesh_lat.ravel(), mesh_lon.ravel())

        water = {
            EARTH.biome_code("earth.ocean"),
            EARTH.biome_code("earth.lake"),
            EARTH.biome_code("earth.river"),
        }
        land = ~np.isin(codes, list(water)).reshape(mesh_lat.shape)
        weights = np.cos(np.deg2rad(mesh_lat))
        non_polar = np.abs(mesh_lat) < EARTH.pole_latitude_deg
        fraction = float(
            np.sum(land[non_polar] * weights[non_polar])
            / np.sum(weights[non_polar])
        )

        self.assertGreater(fraction, 0.18)
        self.assertLess(fraction, 0.38)

    def test_major_continents_islands_and_seaways_are_present(self) -> None:
        land_points = [
            ("North America", 45.0, -100.0),
            ("South America", -15.0, -60.0),
            ("Europe", 50.0, 15.0),
            ("Asia", 48.0, 95.0),
            ("Africa", 2.0, 20.0),
            ("Australia", -25.0, 134.0),
            ("Sinai land bridge", 30.0, 32.0),
            ("Java", -7.0, 110.0),
            ("Indonesia", -3.0, 120.0),
            ("Japan", 37.0, 138.0),
            ("Philippines", 13.0, 122.0),
            ("Madagascar", -20.0, 47.0),
            ("New Zealand", -41.0, 173.0),
            ("British Isles", 54.0, -3.0),
        ]
        ocean_points = [
            ("Pacific", 0.0, -140.0),
            ("Atlantic", 20.0, -40.0),
            ("Indian Ocean", -30.0, 80.0),
        ]

        ocean = EARTH.biome_code("earth.ocean")
        lat = np.array([point[1] for point in land_points + ocean_points])
        lon = np.array([point[2] for point in land_points + ocean_points])
        codes, _ = synthetic_earth.fields(EARTH, lat, lon)

        for i, (name, _, _) in enumerate(land_points):
            self.assertNotEqual(int(codes[i]), ocean, f"{name} should be land")
        for offset, (name, _, _) in enumerate(ocean_points, start=len(land_points)):
            self.assertEqual(int(codes[offset]), ocean, f"{name} should be ocean")

    def test_lakes_rivers_and_mountains_exist(self) -> None:
        lat = np.arange(70.0, -60.0, -0.5)
        lon = np.arange(-180.0, 180.0, 0.5)
        mesh_lat, mesh_lon = np.meshgrid(lat, lon, indexing="ij")
        codes, elevation = synthetic_earth.fields(EARTH, mesh_lat.ravel(), mesh_lon.ravel())
        ids = biome_ids(codes)

        self.assertIn("earth.lake", ids)
        self.assertIn("earth.river", ids)
        self.assertIn("earth.mountains", ids)

        lake = EARTH.biome_code("earth.lake")
        self.assertLess(float(np.max(elevation[codes == lake])), 0.0)

    def test_elevation_has_no_per_hex_noise(self) -> None:
        lon = np.arange(-180.0, 180.0, 0.05)
        _, elevation = synthetic_earth.fields(EARTH, np.full(lon.size, 45.0), lon)
        steps = np.abs(np.diff(elevation))

        self.assertLess(float(np.mean(steps)), 20.0)
        self.assertLess(float(np.percentile(steps, 99)), 200.0)
        self.assertLess(float(np.max(steps)), 200.0)

    def test_imagery_colour_is_deterministic_and_water_is_blue(self) -> None:
        lat = np.array([0.0, 45.0, -25.0, -80.0])
        lon = np.array([-140.0, -100.0, 134.0, 0.0])

        first = synthetic_earth.rgb(EARTH, lat, lon)
        second = synthetic_earth.rgb(EARTH, lat, lon)
        np.testing.assert_array_equal(first, second)

        self.assertGreater(int(first[0, 2]), int(first[0, 0]))
        self.assertGreater(int(first[3, 0]), 200)

    def test_sample_fills_a_grid_and_region_dominants(self) -> None:
        grid = hexify.enumerate_grid(0, 1)
        codes, elevation = synthetic_earth.sample(EARTH, grid)

        self.assertEqual(codes.shape, (grid.total,))
        self.assertEqual(elevation.shape, (grid.total,))
        self.assertTrue(np.all(codes >= 0))
        self.assertTrue(np.all(codes < len(EARTH.biomes)))
        self.assertTrue(np.all(np.isfinite(elevation)))

        dominant = hexify.region_dominant(
            grid, codes, priority=list(range(len(EARTH.biomes)))
        )
        self.assertEqual(dominant.shape, (grid.region_ids.size,))
        self.assertTrue(np.all(dominant >= 0))


if __name__ == "__main__":
    unittest.main()
