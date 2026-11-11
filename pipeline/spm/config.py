"""Filesystem layout and format constants for the pipeline."""

from __future__ import annotations

from pathlib import Path

# pipeline/spm/config.py -> pipeline -> repo root
PIPELINE_DIR = Path(__file__).resolve().parent.parent
REPO_DIR = PIPELINE_DIR.parent

# Downloaded source datasets (never committed).
CACHE_DIR = PIPELINE_DIR / ".cache"

# Baked tiles consumed by the game (never committed).
DATA_DIR = REPO_DIR / "data"

# ---------------------------------------------------------------------------
# Tile format
# ---------------------------------------------------------------------------

#: Magic bytes at the start of every fine-hex tile file.
TILE_MAGIC = b"SPMF"

#: Current tile format version. Bump on any layout change.
TILE_FORMAT_VERSION = 1

#: Bytes per fine-hex record: biome(u8) + elevation_m(i16) + crater(u8).
RECORD_BYTES = 4

#: Per-region tile header size: magic(4) + version(u16) + fineRes(u16) +
#: regionRes(u16) + reserved(u16) + hexCount(u32) + reserved(u32).
TILE_HEADER_BYTES = 20
