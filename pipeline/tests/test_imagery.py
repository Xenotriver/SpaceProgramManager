"""Unit tests for the presentation-only imagery sidecar baker."""

from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from h3.api import basic_int as h3

PIPELINE_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PIPELINE_DIR))

from spm import config, hexify, imagery  # noqa: E402
from spm.bodies import MOON  # noqa: E402


def tiny_grid(region_ids: list[int], counts: list[int]) -> hexify.Grid:
    offsets = np.concatenate([[0], np.cumsum(counts, dtype=np.int64)])
    hex_ids = np.arange(int(offsets[-1]), dtype=np.int64)
    sorted_hex = np.sort(hex_ids)
    order = np.argsort(hex_ids, kind="stable").astype(np.int32)
    return hexify.Grid(
        region_resolution=3,
        fine_resolution=6,
        region_ids=np.asarray(region_ids, dtype=np.int64),
        hex_ids=hex_ids,
        offsets=offsets,
        sorted_hex=sorted_hex,
        order=order,
    )


class ImageryTests(unittest.TestCase):
    def test_align_rgb_rolls_minus_180_180_onto_zero_360(self) -> None:
        src = np.zeros((2, 4, 3), dtype=np.uint8)
        src[:, 0] = (255, 0, 0)  # -180E
        src[:, 2] = (0, 255, 0)  # 0E

        out = imagery.align_rgb(src)

        np.testing.assert_array_equal(out[0, 0], (0, 255, 0))
        np.testing.assert_array_equal(out[0, 2], (255, 0, 0))
        self.assertFalse(np.shares_memory(src, out))

    def test_sampler_uses_pixel_centres_and_rolls_longitude(self) -> None:
        rgb = np.zeros((2, 4, 3), dtype=np.uint8)
        aligned = imagery.align_rgb(rgb)
        aligned[0, 0] = (1, 2, 3)
        aligned[1, 3] = (9, 8, 7)
        sample = imagery.make_sampler(aligned)

        np.testing.assert_array_equal(sample(np.array([90.0]), np.array([0.0]))[0], (1, 2, 3))
        np.testing.assert_array_equal(sample(np.array([-90.0]), np.array([270.0]))[0], (9, 8, 7))

    def test_validate_manifest_rejects_wrong_body(self) -> None:
        grid = tiny_grid([1], [4])
        manifest = {
            "format": "spm-region-tiles",
            "version": config.TILE_FORMAT_VERSION,
            "body": {"key": "mars"},
            "fineResolution": 6,
            "regionResolution": 3,
            "tilesFile": "fine.bin",
            "regions": [{"hexCount": 4, "length": 100}],
        }
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "fine.bin").write_bytes(b"x" * 100)
            with self.assertRaisesRegex(ValueError, "manifest body"):
                imagery.validate_manifest(manifest, MOON, grid, Path(tmp))

    def test_write_sidecar_does_not_modify_manifest(self) -> None:
        grid = tiny_grid([1], [4])
        rgb = np.arange(12, dtype=np.uint8).reshape(4, 3)
        source = {"name": "test", "synthetic": True}
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            manifest_path = out / "manifest.json"
            original = b'{"body":{"key":"moon"}}'
            manifest_path.write_bytes(original)

            color_path, imagery_path = imagery.write_sidecar(
                rgb,
                MOON,
                grid,
                out,
                source=source,
                generated_utc="1970-01-01T00:00:00+00:00",
            )

            self.assertEqual(color_path.read_bytes(), rgb.tobytes())
            self.assertEqual(manifest_path.read_bytes(), original)
            meta = json.loads(imagery_path.read_text(encoding="utf-8"))
            self.assertEqual(meta["format"], imagery.IMAGERY_FORMAT)
            self.assertEqual(meta["version"], imagery.IMAGERY_VERSION)
            self.assertEqual(meta["body"]["key"], "moon")
            self.assertEqual(meta["hexCount"], 4)
            self.assertEqual(meta["bytesPerHex"], 3)
            self.assertEqual(meta["order"], "fine.bin child order")
            self.assertEqual(
                meta["colorSha256"], hashlib.sha256(rgb.tobytes()).hexdigest().upper()
            )

    def test_synthetic_rgb_is_deterministic_and_body_shaped(self) -> None:
        regions = np.asarray(
            h3.cell_to_children(h3.get_res0_cells()[0], 3)[:4], dtype=np.int64
        )
        grid = tiny_grid(list(map(int, regions)), [3, 4, 3, 4])

        first = imagery.synthetic_rgb(MOON, grid)
        second = imagery.synthetic_rgb(MOON, grid)

        self.assertEqual(first.shape, (14, 3))
        self.assertEqual(first.dtype, np.uint8)
        np.testing.assert_array_equal(first, second)
        self.assertTrue(np.all(first[:, 0] == first[:, 1]))
        self.assertTrue(np.all(first[:, 1] == first[:, 2]))


if __name__ == "__main__":
    unittest.main()
