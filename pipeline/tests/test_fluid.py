"""Unit tests for the presentation-only fluid sidecar baker."""

from __future__ import annotations

import hashlib
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from h3.api import basic_int as h3

PIPELINE_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PIPELINE_DIR))

from spm import config, fluid, hexify, imagery  # noqa: E402
from spm.bodies import EARTH, MOON  # noqa: E402


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


def manifest_for(body_key: str, region_id: int, count: int) -> dict:
    length = config.TILE_HEADER_BYTES + count * config.RECORD_BYTES
    return {
        "format": "spm-region-tiles",
        "version": config.TILE_FORMAT_VERSION,
        "body": {"key": body_key},
        "fineResolution": 6,
        "regionResolution": 3,
        "tilesFile": "fine.bin",
        "regions": [
            {"index": str(region_id), "hexCount": count, "offset": 0, "length": length}
        ],
    }


class FluidTests(unittest.TestCase):
    def test_earth_is_registered_with_authoritative_water_biomes(self) -> None:
        water = {b.id for b in EARTH.biomes if b.water_kind != "none"}
        self.assertEqual(water, {"earth.ocean", "earth.lake", "earth.river"})
        self.assertFalse(EARTH.has_crater_source)

    def test_validate_manifest_rejects_wrong_body(self) -> None:
        grid = tiny_grid([1], [4])
        manifest = manifest_for("moon", 1, 4)
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            (out / "fine.bin").write_bytes(b"x" * 100)
            with self.assertRaisesRegex(ValueError, "manifest body"):
                imagery.validate_manifest(manifest, EARTH, grid, out)

    def test_write_sidecar_does_not_modify_manifest(self) -> None:
        grid = tiny_grid([1], [4])
        values = np.array([0, 10, 19, 0], dtype=np.uint8)
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            manifest_path = out / "manifest.json"
            original = b'{"body":{"key":"earth"}}'
            manifest_path.write_bytes(original)

            fluid_path, meta_path = fluid.write_sidecar(
                values,
                EARTH,
                grid,
                out,
                source={"name": "test", "synthetic": True},
                generated_utc="1970-01-01T00:00:00+00:00",
            )

            self.assertEqual(fluid_path.read_bytes(), values.tobytes())
            self.assertEqual(manifest_path.read_bytes(), original)
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            self.assertEqual(meta["format"], fluid.FLUID_FORMAT)
            self.assertEqual(meta["version"], fluid.FLUID_VERSION)
            self.assertEqual(meta["body"]["key"], "earth")
            self.assertEqual(meta["hexCount"], 4)
            self.assertEqual(meta["bytesPerHex"], 1)
            self.assertEqual(meta["order"], "fine.bin child order")
            self.assertEqual(
                meta["fluidSha256"], hashlib.sha256(values.tobytes()).hexdigest().upper()
            )

    def test_synthetic_fluid_marks_only_river_hexes(self) -> None:
        region = int(h3.get_res0_cells()[0])
        grid = tiny_grid([region], [4])
        river = EARTH.biome_code("earth.river")
        codes = np.array([0, river, river, 4], dtype=np.uint8)

        first = fluid.synthetic_fluid(EARTH, grid, codes)
        second = fluid.synthetic_fluid(EARTH, grid, codes)

        np.testing.assert_array_equal(first, second)
        self.assertEqual(first[0], 0)
        self.assertEqual(first[3], 0)
        self.assertTrue(np.all(first[1:3] > 0))
        self.assertTrue(np.all((first[1:3] & fluid._DIRECTION_MASK) != 0))

    def test_read_biome_codes_reads_first_byte_of_each_record(self) -> None:
        region = int(h3.get_res0_cells()[0])
        grid = tiny_grid([region], [4])
        manifest = manifest_for("earth", region, 4)
        records = [
            (0, 0, 0, 0),
            (EARTH.biome_code("earth.river"), 0, 0, 0),
            (EARTH.biome_code("earth.ocean"), 0, 0, 0),
            (EARTH.biome_code("earth.plains"), 0, 0, 0),
        ]
        tile = struct.pack("<4sHHHHII", config.TILE_MAGIC, 1, 6, 3, 0, 4, 0)
        tile += b"".join(struct.pack("<4B", *record) for record in records)

        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            (out / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            (out / "fine.bin").write_bytes(tile)
            codes = fluid.read_biome_codes(out, grid)

        np.testing.assert_array_equal(codes, [0, 2, 0, 4])


if __name__ == "__main__":
    unittest.main()
