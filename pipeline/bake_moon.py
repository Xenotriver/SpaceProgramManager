"""Entry point: bake the Moon tiles.

    python bake_moon.py --dem 16          # fast smoke test
    python bake_moon.py --dem 64 --jobs 4 # full-fidelity bake
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spm.cli import main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(main())
