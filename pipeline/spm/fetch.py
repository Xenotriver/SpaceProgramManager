"""Download and cache public source datasets.

All sources are public-domain / freely redistributable (design doc §5.1):

* **LOLA GDR DEM** - NASA PDS (public domain).
* **USGS Unified Geologic Map of the Moon, 2020** - USGS, CC0.
* **Robbins (2019) lunar crater catalogue** - USGS Astrogeology, PDS.
* **MOLA MEGDR 16 ppd DEM** - NASA PDS (public domain).
* **USGS Mars 15M Geologic Map GIS Renovation** - USGS Astrogeology.
* **Robbins & Hynek (2012) global Mars crater catalogue** - MIT-licensed
  mirror of the published catalogue.
* **NASA SVS CGI Moon Kit colour** and **USGS Viking MDIM 2.1 colorized
  Mars mosaic** - optional presentation-only imagery sidecars.

Nothing here is committed; everything lands in ``pipeline/.cache``.
"""

from __future__ import annotations

import sys
import hashlib
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
    sha256: str | None = None


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
    # Optional presentation-only colour sidecars.  SHA-256 is verified by
    # spm.imagery after the download; sizes are pinned here for the cache.
    "moon_color": Source(
        "moon_color",
        "https://svs.gsfc.nasa.gov/vis/a000000/a004700/a004720/lroc_color_poles_2k.tif",
        "lroc_color_poles_2k.tif",
        3339438,
    ),
    "mars_color": Source(
        "mars_color",
        "https://astrogeology.usgs.gov/ckan/dataset/"
        "7131d503-cdc9-45a5-8f83-5126c0fd397e/resource/"
        "5ea881c6-01b3-41fa-a7af-42d2131b54f1/download/"
        "mars_viking_mdim21_clrmosaic_1km.jpg",
        "mars_viking_mdim21_clrmosaic_1km.jpg",
        36708552,
    ),
    # Real Earth colour; the same pinned file feeds the tile land-cover proxy
    # and the presentation-only color.bin sidecar.
    "earth_color": Source(
        "earth_color",
        "https://assets.science.nasa.gov/content/dam/science/esd/eo/images/bmng/"
        "bmng-topography-bathymetry/december/"
        "world.topo.bathy.200412.3x21600x10800.jpg",
        "world.topo.bathy.200412.3x21600x10800.jpg",
        29868040,
        "3006C58B1272362DB0A8C2DF02DC07CEA4B12DFE820B7DC4A159A075CAF5D4D4",
    ),
    # Real Earth coastlines, lakes, rivers and relief.  All four are public
    # domain and pinned by SHA-256 so a re-bake is reproducible.
    "earth_land": Source(
        "earth_land",
        "https://naturalearth.s3.amazonaws.com/10m_physical/ne_10m_land.zip",
        "ne_10m_land.zip",
        3269070,
        "E547D749445EAA0964ABA76738090EC88F5E63C4585122170F98C67A7EA922DC",
    ),
    "earth_lakes": Source(
        "earth_lakes",
        "https://naturalearth.s3.amazonaws.com/10m_physical/ne_10m_lakes.zip",
        "ne_10m_lakes.zip",
        2349685,
        "0803A06F9C3CB4671D89B68C48B142AAD9366BA40F665245E12A913FBC61722A",
    ),
    "earth_rivers": Source(
        "earth_rivers",
        "https://naturalearth.s3.amazonaws.com/10m_physical/"
        "ne_10m_rivers_lake_centerlines.zip",
        "ne_10m_rivers_lake_centerlines.zip",
        2079507,
        "DED71B01870855CCFE19B51F2EC14C9BB48FAE23C0E9F3C11974D426433B5C38",
    ),
    "earth_elevation": Source(
        "earth_elevation",
        "https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO1/data/ice_surface/"
        "grid_registered/netcdf/ETOPO1_Ice_g_gmt4.grd.gz",
        "ETOPO1_Ice_g_gmt4.grd.gz",
        395148066,
        "C241DB385D89F3D0C22EEC8138224DB9F2EBEE6A1B4D1ECB4EBC6A5743A28EC8",
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

    def verified(path: Path) -> bool:
        if source.sha256 is None:
            return source.size is None or path.stat().st_size == source.size
        if source.size is not None and path.stat().st_size != source.size:
            return False
        digest = hashlib.sha256()
        with path.open("rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                digest.update(chunk)
        return digest.hexdigest().upper() == source.sha256.upper()

    if dest.exists() and not force:
        if verified(dest):
            return dest
        print(f"  cache checksum/size mismatch for {dest.name}; re-downloading")

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
    if source.sha256 is not None and not verified(tmp):
        tmp.unlink(missing_ok=True)
        raise ValueError(f"{source.name}: downloaded SHA-256 does not match the pinned checksum")
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
