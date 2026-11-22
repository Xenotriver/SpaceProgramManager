"""Entry point: bake the optional presentation-only fluid sidecar.

    python bake_fluid.py --body=earth --synthetic
    python bake_fluid.py --synthetic --out data/earth

The normal tile bake must already exist in the output directory.  This script
never rewrites ``manifest.json`` or ``fine.bin``.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spm.fluid import main as cli_main  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    return cli_main(argv)


if __name__ == "__main__":
    raise SystemExit(main())
