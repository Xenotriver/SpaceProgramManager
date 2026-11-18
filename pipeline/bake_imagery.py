"""Entry point: bake optional true-colour sidecars for a body.

    python bake_imagery.py --body=moon --synthetic               # CI fixture
    python bake_imagery.py --body=mars --synthetic --out data/mars
    python bake_imagery.py --body=mars                           # real USGS imagery

The normal tile bake must already exist in the output directory.  This script
never rewrites ``manifest.json`` or ``fine.bin``.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spm.imagery import main as cli_main  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    return cli_main(argv)


if __name__ == "__main__":
    raise SystemExit(main())
