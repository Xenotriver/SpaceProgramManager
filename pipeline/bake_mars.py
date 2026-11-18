"""Entry point: bake the Mars tiles.

    python bake_mars.py --synthetic                    # CI fixture, no network
    python bake_mars.py --synthetic --out data/mars    # same, explicit output

Real MOLA MEGDR elevation baking lands once the Mars source URLs are verified;
until then the un-synthetic path fails with an actionable message.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spm.cli import main as cli_main  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    if not any(arg == "--body" or arg.startswith("--body=") for arg in args):
        args.insert(0, "--body=mars")
    return cli_main(args)


if __name__ == "__main__":
    raise SystemExit(main())
