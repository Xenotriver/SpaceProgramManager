"""Entry point: bake the Earth tiles.

    python bake_earth.py                                # real sources, cached
    python bake_earth.py --synthetic                    # CI fixture, no network
    python bake_earth.py --out data/earth --force       # re-download sources

Real bakes use pinned Natural Earth vectors, NOAA ETOPO relief and NASA Blue
Marble colour.  Every source is checksum-verified and cached under
``pipeline/.cache/earth``; ``--synthetic`` keeps the deterministic CI fixture's
tile and colour bytes unchanged.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spm.cli import main as cli_main  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    if not any(arg == "--body" or arg.startswith("--body=") for arg in args):
        args.insert(0, "--body=earth")
    return cli_main(args)


if __name__ == "__main__":
    raise SystemExit(main())
