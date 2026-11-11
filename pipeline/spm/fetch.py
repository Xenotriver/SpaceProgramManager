"""Download and cache public source datasets.

All sources are public-domain / freely redistributable (design doc §5.1):

* **LOLA GDR DEM** - NASA PDS (public domain).
* **USGS Unified Geologic Map of the Moon, 2020** - USGS, CC0.
* **Robbins (2019) lunar crater catalogue** - USGS Astrogeology, PDS.

Nothing here is committed; everything lands in ``pipeline/.cache``.
"""

from __future__ import annotations

import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

from . import config


@dataclass(frozen=True)
class Source:
    name: str
    url: str
    filename: str
    size: int | None = None


SOURCES: dict[str, Source] = {
    "lola_16": Source(
        "lola_16",
        "https://imbrium.mit.edu/DATA/LOLA_GDR/CYLINDRICAL/IMG/LDEM_16.IMG",
        "LDEM_16.IMG",
        33177600,
    ),
    "lola_16_lbl": Source(
        "lola_16_lbl",
        "https://imbrium.mit.edu/DATA/LOLA_GDR/CYLINDRICAL/IMG/LDEM_16.LBL",
        "LDEM_16.LBL",
    ),
    "lola_64": Source(
        "lola_64",
        "https://imbrium.mit.edu/DATA/LOLA_GDR/CYLINDRICAL/IMG/LDEM_64.IMG",
        "LDEM_64.IMG",
        531697920,
    ),
    "lola_64_lbl": Source(
        "lola_64_lbl",
        "https://imbrium.mit.edu/DATA/LOLA_GDR/CYLINDRICAL/IMG/LDEM_64.LBL",
        "LDEM_64.LBL",
    ),
    "ugm_raster": Source(
        "ugm_raster",
        "https://asc-astropedia.s3.us-west-2.amazonaws.com/Moon/Geology/Ancillary/"
        "unified_geologic_map_of_the_moon_raster.zip",
        "ugm_raster.zip",
        36992007,
    ),
    "robbins": Source(
        "robbins",
        "https://astrogeology.usgs.gov/ckan/dataset/"
        "f89f5478-b69a-486c-b9b5-30d7b0c5ad2b/resource/"
        "c4f25cc2-4f8a-4207-a845-5e176da3ac5a/download/"
        "lunar_crater_database_robbins_2018",
        "robbins_2018.zip",
    ),
}


def cache_dir(body_key: str = "moon") -> Path:
    path = config.CACHE_DIR / body_key
    path.mkdir(parents=True, exist_ok=True)
    return path


def fetch(name: str, *, force: bool = False) -> Path:
    """Download ``name`` into the cache (idempotent) and return its path."""
    import urllib.request

    source = SOURCES[name]
    dest = cache_dir() / source.filename
    if dest.exists() and not force:
        if source.size is None or dest.stat().st_size == source.size:
            return dest
        print(f"  cache size mismatch for {dest.name}; re-downloading")

    print(f"  downloading {source.name} -> {dest.name}")
    tmp = dest.with_suffix(dest.suffix + ".part")
    with urllib.request.urlopen(source.url) as response, open(tmp, "wb") as fh:
        total = int(response.headers.get("Content-Length", 0))
        done = 0
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            fh.write(chunk)
            done += len(chunk)
            if total:
                pct = 100.0 * done / total
                sys.stdout.write(f"\r    {pct:5.1f}%  {done >> 20} MiB")
                sys.stdout.flush()
    sys.stdout.write("\n")
    tmp.replace(dest)
    return dest


def _extract(zip_path: Path, members: dict[str, str]) -> dict[str, Path]:
    """Extract ``{member_suffix: output_name}`` from a zip into a sibling dir."""
    out_dir = zip_path.with_suffix("")
    out_dir.mkdir(parents=True, exist_ok=True)
    result: dict[str, Path] = {}
    with zipfile.ZipFile(zip_path) as zf:
        names = zf.namelist()
        for suffix, out_name in members.items():
            match = next((n for n in names if n.endswith(suffix)), None)
            if match is None:
                raise FileNotFoundError(f"{suffix!r} not found in {zip_path.name}")
            target = out_dir / out_name
            with zf.open(match) as src, open(target, "wb") as dst:
                dst.write(src.read())
            result[out_name] = target
    return result


def ensure_moon_inputs(dem_ppd: int, *, force: bool = False) -> dict[str, Path]:
    """Ensure every Moon input exists and return a name -> path mapping."""
    paths: dict[str, Path] = {}

    dem = f"lola_{dem_ppd}"
    if dem not in SOURCES:
        raise ValueError(f"unsupported DEM resolution {dem_ppd} (use 4, 16, 64, ...)")
    paths["dem"] = fetch(dem, force=force)
    paths["dem_label"] = fetch(f"{dem}_lbl", force=force)

    ugm_zip = fetch("ugm_raster", force=force)
    ugm = _extract(
        ugm_zip,
        {
            "numerical_versions/Unified_Geologic_Map_of_the_Moon_64ppd.tif": "geology.tif",
        },
    )
    paths["geology"] = ugm["geology.tif"]

    robbins_zip = fetch("robbins", force=force)
    rob = _extract(
        robbins_zip,
        {"data/lunar_crater_database_robbins_2018.csv": "craters.csv"},
    )
    paths["craters"] = rob["craters.csv"]

    return paths
