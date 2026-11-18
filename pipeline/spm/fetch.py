"""Download and cache public source datasets.

All sources are public-domain / freely redistributable (design doc §5.1):

* **LOLA GDR DEM** - NASA PDS (public domain).
* **USGS Unified Geologic Map of the Moon, 2020** - USGS, CC0.
* **Robbins (2019) lunar crater catalogue** - USGS Astrogeology, PDS.
* **MOLA MEGDR 16 ppd DEM** - NASA PDS (public domain).
* **USGS Mars 15M Geologic Map GIS Renovation** - USGS Astrogeology.
* **Robbins & Hynek (2012) global Mars crater catalogue** - MIT-licensed
  mirror of the published catalogue.

Nothing here is committed; everything lands in ``pipeline/.cache``.
"""

from __future__ import annotations

import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

import requests

from . import config
from .bodies import BodyDef


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
        530841600,
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
    "mola_16_img": Source(
        "mola_16_img",
        "https://pds-geosciences.wustl.edu/mgs/"
        "urn-nasa-pds-mgs_mola_topography_derived/meg016/megt90n000eb.img",
        "megt90n000eb.img",
        33177600,
    ),
    "mola_16_lbl": Source(
        "mola_16_lbl",
        "https://pds-geosciences.wustl.edu/mgs/"
        "urn-nasa-pds-mgs_mola_topography_derived/meg016/megt90n000eb.xml",
        "megt90n000eb.xml",
        15528,
    ),
    "mars_geology": Source(
        "mars_geology",
        "https://astrogeology.usgs.gov/ckan/dataset/"
        "a1c6ca2e-c18c-4059-80cc-9a75ae0bf807/resource/"
        "16558096-90a2-46c5-8a99-d5aa868a7ec4/download/"
        "mars15mgeologicgisrenovation.zip",
        "mars15mgeologicgisrenovation.zip",
        15435129,
    ),
    "mars_craters": Source(
        "mars_craters",
        "https://raw.githubusercontent.com/acbuie/craterz/main/data/craterz.csv",
        "craters.csv",
        # GitHub serves this with gzip when the client asks for it; the
        # decompressed CSV is ~97 MB, so do not pin a cache size here.
    ),
}


def cache_dir(body_key: str = "moon") -> Path:
    path = config.CACHE_DIR / body_key
    path.mkdir(parents=True, exist_ok=True)
    return path


def fetch(name: str, *, force: bool = False, body_key: str = "moon") -> Path:
    """Download ``name`` into ``body_key``'s cache and return its path.

    ``body_key`` defaults to ``moon`` so the existing Moon bake keeps using the
    same cache directory it always has.  Requests supplies the bundled CA
    certificates that some PDS hosts need on Windows.
    """
    source = SOURCES[name]
    dest = cache_dir(body_key) / source.filename
    if dest.exists() and not force:
        if source.size is None or dest.stat().st_size == source.size:
            return dest
        print(f"  cache size mismatch for {dest.name}; re-downloading")

    print(f"  downloading {source.name} -> {dest.name}")
    tmp = dest.with_suffix(dest.suffix + ".part")
    headers = {"User-Agent": "spm-pipeline/1.0", "Accept-Encoding": "identity"}
    with requests.get(source.url, stream=True, timeout=120, headers=headers) as response:
        response.raise_for_status()
        with open(tmp, "wb") as fh:
            total = int(response.headers.get("Content-Length", 0))
            done = 0
            for chunk in response.iter_content(1 << 20):
                if not chunk:
                    continue
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


def ensure_mars_inputs(dem_ppd: int, *, force: bool = False) -> dict[str, Path]:
    """Ensure every Mars input exists and return a name -> path mapping."""
    if dem_ppd != 16:
        raise ValueError("Mars uses MOLA MEGDR 16 ppd only; pass --dem 16")

    paths: dict[str, Path] = {}
    paths["dem"] = fetch("mola_16_img", body_key="mars", force=force)
    paths["dem_label"] = fetch("mola_16_lbl", body_key="mars", force=force)

    geology_zip = fetch("mars_geology", body_key="mars", force=force)
    geology = _extract(
        geology_zip,
        {
            "geo_units_oc_dd.shp": "geology.shp",
            "geo_units_oc_dd.shx": "geology.shx",
            "geo_units_oc_dd.dbf": "geology.dbf",
            "geo_units_oc_dd.prj": "geology.prj",
        },
    )
    paths["geology"] = geology["geology.shp"]

    paths["craters"] = fetch("mars_craters", body_key="mars", force=force)
    return paths


def ensure_inputs(
    body: BodyDef,
    dem_ppd: int,
    *,
    force: bool = False,
) -> dict[str, Path]:
    """Ensure every real input for ``body`` exists.

    Dispatch is by :attr:`BodyDef.source_profile`.  The Moon profile keeps the
    exact legacy download/extract path; Mars uses MOLA, the USGS 15M geologic
    map and the Robbins & Hynek crater catalogue.
    """
    if body.source_profile == "moon":
        return ensure_moon_inputs(dem_ppd, force=force)

    if body.source_profile == "mars":
        return ensure_mars_inputs(dem_ppd, force=force)

    raise ValueError(f"unknown source profile {body.source_profile!r} for {body.key!r}")
