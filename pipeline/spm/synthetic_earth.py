"""Deterministic synthetic Earth surface field for the CI fixture.

The fixture is deliberately *procedural*: it is not geography and it never
pretends to be.  It does, however, need to read like a planet.  The previous
fixture used a union of perfect ellipses sampled once per region, which gave
blobby continents and allowed no islands at all.  This module builds a smooth,
seamless lat/lon field from fixed seeds:

* warped fBm noise perturbs the continent outlines, so coasts have bays,
  peninsulas and archipelagos;
* small seed ellipses add the mid-sized islands that must not disappear;
* elevation is a continuous field with broad basins, shelves, lowlands and
  mountain belts -- there is no per-hex random noise;
* rivers are meandering curves and lakes are warped ellipses, so both stay on
  land and line up with the fluid sidecar.

Everything is sampled per fine hex by :func:`sample`, which keeps the existing
res-6/res-3 layout intact.  The field functions are shared with the imagery
baker so colour, biome and elevation agree.
"""

from __future__ import annotations

import math

import numpy as np
from h3.api import basic_int as h3

from .bodies import BodyDef

# Rough continent anchors -- centres only.  The noise field supplies the shape.
# (latitude, longitude, lat radius, lon radius, weight)
LANDMASSES: tuple[tuple[float, float, float, float, float], ...] = (
    (46.0, -102.0, 29.0, 52.0, 1.00),  # North America
    (15.0, -88.0, 6.0, 13.0, 0.98),    # Central America
    (-8.0, -60.0, 33.0, 21.0, 1.00),   # South America
    (73.0, -42.0, 12.0, 23.0, 1.00),   # Greenland
    (52.0, 14.0, 17.0, 37.0, 1.00),    # Europe
    (49.0, 92.0, 26.0, 72.0, 1.00),    # Asia
    (22.0, 78.0, 12.0, 13.0, 1.00),    # India
    (15.0, 104.0, 11.0, 16.0, 1.00),   # Indochina
    (3.0, 20.0, 34.0, 30.0, 1.00),     # Africa
    (24.0, 45.0, 12.0, 14.0, 0.98),    # Arabia (joins Africa to Asia)
    (-25.0, 134.0, 17.0, 22.0, 1.00),  # Australia
    (-82.0, 0.0, 10.0, 180.0, 1.00),   # Antarctica

    # Mid-sized islands.  These are the ones a res-3 globe can still show.
    (54.0, -3.0, 6.0, 3.5, 0.95),      # British Isles
    (37.0, 138.0, 9.0, 3.0, 0.95),     # Japan
    (13.0, 122.0, 8.0, 4.0, 0.95),     # Philippines
    (-3.0, 110.0, 5.0, 9.0, 0.95),     # Sumatra / Borneo / Java
    (-3.0, 121.0, 4.0, 5.0, 0.95),     # Sulawesi
    (-6.0, 142.0, 4.0, 6.0, 0.95),     # New Guinea
    (-20.0, 47.0, 10.0, 3.5, 0.95),    # Madagascar
    (7.0, 81.0, 2.5, 2.5, 0.85),       # Sri Lanka
    (-41.0, 172.0, 10.0, 5.0, 0.99),   # New Zealand
    (-43.0, 147.0, 3.0, 3.0, 0.85),    # Tasmania
    (65.0, -19.0, 3.0, 6.0, 0.90),     # Iceland
)

# (lat, lon, lat radius, lon radius, peak height metres)
MOUNTAIN_BELTS: tuple[tuple[float, float, float, float, float], ...] = (
    (-20.0, -70.0, 34.0, 4.0, 3200.0),  # Andes
    (32.0, 84.0, 7.0, 21.0, 3600.0),    # Himalaya / Tibet
    (42.0, -112.0, 17.0, 6.0, 2400.0),  # Rockies
    (46.0, 10.0, 4.0, 8.0, 1700.0),     # Alps
    (-10.0, 36.0, 12.0, 4.0, 1500.0),   # East African Rift
    (36.0, 72.0, 8.0, 10.0, 2600.0),    # Pamir / Hindu Kush
)

# Warped ellipses of standing water.  Only applied where the land field is land.
LAKES: tuple[tuple[float, float, float, float], ...] = (
    (47.0, -87.0, 4.0, 7.0),   # Great Lakes
    (-2.0, 32.0, 6.0, 4.0),    # East African lakes
    (53.0, 108.0, 4.0, 3.0),   # Lake Baikal
    (36.0, 140.0, 2.0, 2.0),   # Lake Kasumigaura
)

# Curved rivers: (axis, start, end, cross-coordinate, slope, amplitude, width).
# ``axis='lon'`` means longitude is the independent coordinate; ``axis='lat'``
# means latitude is.  The cross coordinate is the other axis at the source.
RIVERS: tuple[tuple[str, float, float, float, float, float, float], ...] = (
    ("lon", -74.0, -50.0, -5.0, 0.03, 1.8, 1.15),    # Amazon
    ("lon", 8.0, 30.0, 45.0, -0.09, 1.4, 0.85),      # Danube
    ("lon", 90.0, 122.0, 31.0, -0.04, 1.6, 1.05),    # Yangtze
    ("lon", 73.0, 91.0, 25.0, -0.02, 1.2, 0.85),     # Ganges
    ("lon", 16.0, 30.0, -2.0, 0.03, 1.3, 0.90),      # Congo
    ("lat", 4.0, 31.0, 32.0, 0.10, 2.2, 0.85),       # Nile
    ("lat", 29.0, 47.0, -93.0, 0.06, 1.8, 0.85),     # Mississippi
)

VOLCANOES: tuple[tuple[float, float, float, float], ...] = (
    (65.0, -19.0, 2.5, 5.0),   # Iceland
    (19.5, -155.5, 2.0, 2.5),  # Hawaii
    (41.0, 14.0, 2.0, 2.0),    # Italy
)


def _lon_delta(lon: np.ndarray, centre: float) -> np.ndarray:
    """Shortest signed longitude separation, in degrees."""
    return (np.asarray(lon) - centre + 180.0) % 360.0 - 180.0


def _hash(ix: np.ndarray, iy: np.ndarray, seed: int, period_lon: int) -> np.ndarray:
    """Deterministic 0..1 lattice hash, periodic in longitude."""
    x = np.mod(ix, int(period_lon)).astype(np.uint64)
    y = np.asarray(iy, dtype=np.uint64)
    h = x * np.uint64(0x9E3779B1)
    h ^= y * np.uint64(0x85EBCA77)
    h ^= np.uint64(seed) * np.uint64(0xC2B2AE3D)
    h ^= h >> np.uint64(16)
    h *= np.uint64(0x7FEB352D)
    h ^= h >> np.uint64(15)
    h *= np.uint64(0x846CA68B)
    h ^= h >> np.uint64(16)
    return (h & np.uint64(0xFFFF)).astype(np.float64) / 65535.0


def _value_noise(
    lat: np.ndarray,
    lon: np.ndarray,
    cells_lat: int,
    cells_lon: int,
    seed: int,
) -> np.ndarray:
    """Smooth value noise on a latitude/longitude lattice, seamless at ±180°."""
    x = np.mod(np.asarray(lon) + 180.0, 360.0) / 360.0 * cells_lon
    y = (np.clip(np.asarray(lat), -90.0, 90.0) + 90.0) / 180.0 * cells_lat
    x0 = np.floor(x).astype(np.int64)
    y0 = np.clip(np.floor(y).astype(np.int64), 0, cells_lat - 1)
    y1 = np.minimum(y0 + 1, cells_lat)

    fx = x - x0
    fy = y - y0
    fx = fx * fx * (3.0 - 2.0 * fx)
    fy = fy * fy * (3.0 - 2.0 * fy)

    a = _hash(x0, y0, seed, cells_lon)
    b = _hash(x0 + 1, y0, seed, cells_lon)
    c = _hash(x0, y1, seed, cells_lon)
    d = _hash(x0 + 1, y1, seed, cells_lon)
    return (a * (1.0 - fx) + b * fx) * (1.0 - fy) + (c * (1.0 - fx) + d * fx) * fy


def _fbm(
    lat: np.ndarray,
    lon: np.ndarray,
    cells_lat: int,
    cells_lon: int,
    octaves: int,
    seed: int,
) -> np.ndarray:
    """Fractal value noise in 0..1."""
    total = np.zeros_like(np.asarray(lat), dtype=np.float64)
    weight = 0.0
    amplitude = 1.0
    for octave in range(octaves):
        total += amplitude * _value_noise(
            lat, lon, cells_lat << octave, cells_lon << octave, seed + octave * 101
        )
        weight += amplitude
        amplitude *= 0.5
    return total / weight


def _ellipse(
    lat: np.ndarray,
    lon: np.ndarray,
    centre_lat: float,
    centre_lon: float,
    radius_lat: float,
    radius_lon: float,
) -> np.ndarray:
    """1 at an ellipse centre, 0 on its rim, negative outside."""
    dlat = (np.asarray(lat) - centre_lat) / radius_lat
    dlon = _lon_delta(lon, centre_lon) / radius_lon
    return 1.0 - np.sqrt(dlat * dlat + dlon * dlon)


def land_field(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
    """Continuous land score: >0 land, <0 ocean, near 0 coast."""
    lat = np.asarray(lat, dtype=np.float64)
    lon = np.asarray(lon, dtype=np.float64)

    warp_lat = (_fbm(lat, lon, 4, 8, 3, 11) - 0.5) * 15.0
    warp_lon = (_fbm(lat, lon, 4, 8, 3, 29) - 0.5) * 19.0
    wlat = lat + warp_lat
    wlon = lon + warp_lon

    field = np.full(lat.shape, -1.0, dtype=np.float64)
    for centre_lat, centre_lon, radius_lat, radius_lon, weight in LANDMASSES:
        # Continental cores are deliberately tight; the island and isthmus seeds
        # keep their full radii so the globe still shows the mid-sized islands
        # and Africa remains joined to Eurasia.
        scale = 0.82 if weight >= 1.0 else 1.0
        influence = _ellipse(
            wlat, wlon, centre_lat, centre_lon,
            radius_lat * scale, radius_lon * scale,
        )
        field = np.maximum(field, influence * weight)

    detail = (_fbm(wlat, wlon, 10, 20, 4, 47) - 0.5) * 0.30
    return field + detail


def _mountain_height(
    lat: np.ndarray,
    lon: np.ndarray,
    land: np.ndarray,
) -> np.ndarray:
    height = np.zeros_like(lat, dtype=np.float64)
    for centre_lat, centre_lon, radius_lat, radius_lon, peak in MOUNTAIN_BELTS:
        influence = np.clip(_ellipse(lat, lon, centre_lat, centre_lon, radius_lat, radius_lon), 0, 1)
        if not np.any(influence):
            continue
        rough = 0.65 + 0.70 * _fbm(lat, lon, 16, 32, 3, 71)
        height = np.maximum(height, influence * peak * rough)
    return height * land


def _lake_influence(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
    influence = np.full(np.asarray(lat).shape, -1.0, dtype=np.float64)
    for centre_lat, centre_lon, radius_lat, radius_lon in LAKES:
        influence = np.maximum(
            influence, _ellipse(lat, lon, centre_lat, centre_lon, radius_lat, radius_lon)
        )
    return influence


def _lake_mask(lat: np.ndarray, lon: np.ndarray, land: np.ndarray) -> np.ndarray:
    return land & (_lake_influence(lat, lon) > 0.12)


def _river_mask(lat: np.ndarray, lon: np.ndarray, land: np.ndarray) -> np.ndarray:
    mask = np.zeros(lat.shape, dtype=bool)
    for axis, start, end, cross, slope, amplitude, width in RIVERS:
        if axis == "lon":
            along = np.asarray(lon)
            t = (along - start) / (end - start)
            curve = cross + slope * (along - start) + amplitude * np.sin(math.pi * t * 2.0)
            distance = np.abs(np.asarray(lat) - curve)
            inside = (along >= min(start, end)) & (along <= max(start, end))
            # Fade the river out at both ends instead of cutting it squarely.
            inside &= (t > 0.03) & (t < 0.97)
        else:
            along = np.asarray(lat)
            t = (along - start) / (end - start)
            curve = cross + slope * (along - start) + amplitude * np.sin(math.pi * t * 2.0)
            distance = np.abs(_lon_delta(lon, curve))
            inside = (along >= min(start, end)) & (along <= max(start, end))
            inside &= (t > 0.03) & (t < 0.97)
        mask |= land & inside & (distance < width)
    return mask


def _volcanic_mask(lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
    mask = np.zeros(lat.shape, dtype=bool)
    for centre_lat, centre_lon, radius_lat, radius_lon in VOLCANOES:
        mask |= _ellipse(lat, lon, centre_lat, centre_lon, radius_lat, radius_lon) > 0.25
    return mask


def elevation_m(lat: np.ndarray, lon: np.ndarray, field: np.ndarray) -> np.ndarray:
    """Smooth elevation in metres; no high-frequency or per-hex component."""
    latitude = np.asarray(lat, dtype=np.float64)
    longitude = np.asarray(lon, dtype=np.float64)

    sea_floor = -4100.0 - 900.0 * (_fbm(latitude, longitude, 5, 10, 3, 101) - 0.5)
    lowland = 90.0 + 780.0 * _fbm(latitude, longitude, 8, 16, 3, 131)
    lowland += 260.0 * (_fbm(latitude, longitude, 20, 40, 2, 163) - 0.5)

    # Both sides meet at 0 m on the coast.  Blending the full ocean and land
    # elevations instead used to leave newly emergent land hundreds of metres
    # underwater, which made coastal hills read as trenches.
    ocean_t = np.clip(-field / 0.20, 0.0, 1.0)
    ocean_t = ocean_t * ocean_t * (3.0 - 2.0 * ocean_t)
    land_t = np.clip(field / 0.05, 0.0, 1.0)
    land_t = land_t * land_t * (3.0 - 2.0 * land_t)
    elevation = np.where(field >= 0.0, lowland * land_t, sea_floor * ocean_t)

    # Lake basins are broad, continuous depressions. Setting a fixed lake-bed
    # elevation only inside the biome mask used to drop hundreds of metres at the
    # shoreline, which read as a cliff in the diorama. Blend the whole surface
    # toward a lake plane instead; the biome mask then only decides the colour.
    lake_basin = np.clip(_lake_influence(latitude, longitude) / 0.14, 0.0, 1.0)
    lake_basin = lake_basin * lake_basin * (3.0 - 2.0 * lake_basin)

    mountains = _mountain_height(latitude, longitude, land_t)
    elevation += mountains * (1.0 - lake_basin)

    lake_surface = -140.0 - 80.0 * _fbm(latitude, longitude, 8, 16, 2, 191)
    elevation = elevation * (1.0 - lake_basin) + lake_surface * lake_basin

    lakes = _lake_mask(latitude, longitude, field > 0.0)
    rivers = _river_mask(latitude, longitude, field > 0.0) & ~lakes
    elevation[rivers] = np.maximum(20.0, elevation[rivers] - 60.0)

    # Ice sheets are high and flat, but the pole biome is assigned independently.
    polar = np.abs(latitude) >= 66.5
    elevation[polar] = np.maximum(elevation[polar], 420.0 + 180.0 * _fbm(
        latitude[polar], longitude[polar], 4, 8, 2, 211
    ))
    return elevation.astype(np.float32)


def _biome_codes(body: BodyDef, lat: np.ndarray, lon: np.ndarray, field: np.ndarray,
                 elevation: np.ndarray) -> np.ndarray:
    latitude = np.asarray(lat, dtype=np.float64)
    longitude = np.asarray(lon, dtype=np.float64)
    land = field > 0.0
    code = np.full(latitude.shape, body.biome_code("earth.ocean"), dtype=np.int8)

    def set_biome(biome_id: str, mask: np.ndarray) -> None:
        if biome_id in body.biome_ids and np.any(mask):
            code[mask] = body.biome_code(biome_id)

    shore = (field > -0.012) & (field <= 0.035)
    lakes = _lake_mask(latitude, longitude, land)
    rivers = _river_mask(latitude, longitude, land) & ~lakes
    ground = land & ~shore & ~lakes & ~rivers

    volcanic = _volcanic_mask(latitude, longitude)
    set_biome("earth.volcanic", ground & volcanic)

    mountains = _mountain_height(latitude, longitude, ground.astype(np.float64))
    set_biome("earth.mountains", ground & ~volcanic & (mountains > 950.0))

    tundra = ground & ((latitude >= 58.0) | (latitude <= -55.0))
    taiga = ground & (latitude >= 48.0)
    set_biome("earth.tundra", tundra)
    set_biome("earth.taiga", taiga & ~tundra)

    moisture = _fbm(latitude, longitude, 8, 16, 3, 257)
    habitable = ground & ~tundra & ~taiga & ~volcanic & ~(mountains > 950.0)
    set_biome("earth.forest", habitable & (moisture > 0.54))

    remaining = habitable & (moisture <= 0.54)
    roughness = _fbm(latitude, longitude, 16, 32, 2, 283)
    set_biome("earth.hills", remaining & ((elevation > 420.0) | (roughness > 0.58)))
    set_biome("earth.plains", remaining & ~((elevation > 420.0) | (roughness > 0.58)))

    # Water is authoritative and must win over the land classification.
    set_biome("earth.shore", shore)
    set_biome("earth.lake", lakes)
    set_biome("earth.river", rivers)

    north = body.biome_code(body.pole_north_biome_ids[1])
    south = body.biome_code(body.pole_south_biome_ids[1])
    code[latitude >= body.pole_latitude_deg] = north
    code[latitude <= -body.pole_latitude_deg] = south
    return code


def fields(body: BodyDef, lat: np.ndarray, lon: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Return ``(biome codes, elevation metres)`` for lat/lon arrays."""
    field = land_field(lat, lon)
    elevation = elevation_m(lat, lon, field)
    codes = _biome_codes(body, lat, lon, field, elevation)
    return codes, elevation


# Presentation-only colours.  Earth's imagery sidecar uses these so the globe's
# regional means and the flat views agree with the authoritative biome bytes.
_RGB: dict[str, tuple[int, int, int]] = {
    "earth.ocean": (24, 72, 136),
    "earth.lake": (34, 108, 168),
    "earth.river": (40, 122, 182),
    "earth.shore": (196, 184, 142),
    "earth.plains": (116, 148, 82),
    "earth.hills": (126, 132, 78),
    "earth.mountains": (146, 132, 116),
    "earth.forest": (52, 104, 62),
    "earth.taiga": (66, 100, 78),
    "earth.tundra": (146, 152, 138),
    "earth.pole_north": (236, 242, 248),
    "earth.pole_south": (236, 242, 248),
    "earth.volcanic": (92, 80, 76),
}


def rgb(body: BodyDef, lat: np.ndarray, lon: np.ndarray) -> np.ndarray:
    """Smooth synthetic RGB for the Earth imagery sidecar."""
    codes, _ = fields(body, lat, lon)
    texture = (_fbm(np.asarray(lat), np.asarray(lon), 24, 48, 3, 307) - 0.5) * 16.0
    out = np.empty((np.asarray(lat).size, 3), dtype=np.uint8)
    for code, colour in _RGB.items():
        mask = codes == body.biome_code(code)
        if not np.any(mask):
            continue
        out[mask] = np.clip(np.asarray(colour, dtype=np.float64) + texture[mask, None], 0, 255)
    return out


def latlon_batches(grid, *, batch_regions: int = 64):
    """Yield ``(start, end, lat, lon)`` for bounded batches of fine hexes."""
    n_regions = int(grid.region_ids.size)
    for region_start in range(0, n_regions, batch_regions):
        region_end = min(region_start + batch_regions, n_regions)
        start = int(grid.offsets[region_start])
        end = int(grid.offsets[region_end])
        cells = grid.hex_ids[start:end]

        lat = np.empty(cells.size, dtype=np.float64)
        lon = np.empty(cells.size, dtype=np.float64)
        for i, cell in enumerate(cells):
            lat[i], lon[i] = h3.cell_to_latlng(int(cell))

        yield start, end, lat, lon


def sample(body: BodyDef, grid, *, batch_regions: int = 64) -> tuple[np.ndarray, np.ndarray]:
    """Sample every fine-hex centroid, keeping memory bounded by region batches."""
    codes = np.empty(grid.total, dtype=np.int8)
    elevation = np.empty(grid.total, dtype=np.float32)

    for start, end, lat, lon in latlon_batches(grid, batch_regions=batch_regions):
        codes[start:end], elevation[start:end] = fields(body, lat, lon)

    return codes, elevation


def land_fraction(body: BodyDef, grid, *, batch_regions: int = 64) -> float:
    """Fraction of fine hexes classified as land (diagnostic for tests/fixtures)."""
    codes, _ = sample(body, grid, batch_regions=batch_regions)
    ocean = body.biome_code("earth.ocean")
    lake = body.biome_code("earth.lake")
    river = body.biome_code("earth.river")
    water = (codes == ocean) | (codes == lake) | (codes == river)
    return float(1.0 - np.mean(water))
