"""Celestial body definitions: biome tables and classification parameters.

Biomes are **per body** (design doc §2.2/§2.4): a biome with the same name on
two different bodies is a separate definition with its own rank and build
rules.  The order of :attr:`BodyDef.biomes` is also the deterministic
*priority* order used as the final tie-break in the dominant-biome chain.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class BiomeDef:
    """A single biome of a body."""

    id: str
    display_name: str
    #: Ascending elevation rank used by the movement model.
    elevation_rank: int
    buildable: bool = True
    #: Authoritative water class written to the manifest so Sim can identify
    #: water biomes without hardcoding per-body ids.
    water_kind: str = "none"


@dataclass(frozen=True)
class BodyDef:
    """Everything the pipeline needs to bake one body."""

    key: str
    display_name: str
    radius_km: float
    #: H3 resolution of the buildable gameplay cell.
    fine_resolution: int
    #: H3 resolution of the coarse region tile.
    region_resolution: int
    biomes: tuple[BiomeDef, ...]

    # --- classification parameters (body specific) ---

    #: Geology raster DN values that count as mare (dark, smooth basalt).
    mare_geology_dns: frozenset[int] = frozenset()
    #: Elevation band edges in metres: below -> Lowlands, between -> Midlands,
    #: above -> Highlands.
    lowlands_max_m: float = -1000.0
    midlands_max_m: float = 1000.0
    #: |latitude| at or above which the pole tiers replace the equatorial
    #: Lowlands/Midlands/Highlands names.
    pole_latitude_deg: float = 60.0
    #: Craters smaller than this are ignored when building the overlay.
    crater_min_diameter_km: float = 1.0

    # --- classification role -> biome id ---------------------------------
    # The pipeline uses these roles instead of hardcoding ``luna.*`` ids, so a
    # second body can reuse the same elevation/pole/mare chain.  Defaults keep
    # the Moon table exactly as it was before Mars was added.

    #: Biome assigned where the geology raster marks a mare unit.  ``None``
    #: means the body has no mare classification (Mars).
    mare_biome_id: str | None = "luna.mare"
    lowlands_biome_id: str = "luna.lowlands"
    midlands_biome_id: str = "luna.midlands"
    highlands_biome_id: str = "luna.highlands"
    #: Codes for (lowland, midland, highland) bands inside the north pole.
    pole_north_biome_ids: tuple[str, str, str] = (
        "luna.pole_north_lowlands",
        "luna.pole_north_midlands",
        "luna.pole_north_highlands",
    )
    #: Codes for (lowland, midland, highland) bands inside the south pole.
    pole_south_biome_ids: tuple[str, str, str] = (
        "luna.pole_south_lowlands",
        "luna.pole_south_midlands",
        "luna.pole_south_highlands",
    )
    #: Optional geology-derived biome overrides used by Mars.
    volcanic_biome_id: str | None = None
    canyon_biome_id: str | None = None

    # --- source availability ---------------------------------------------

    #: Selects the reader/fetch dispatch in :mod:`spm.fetch` and :mod:`spm.cli`.
    source_profile: str = "moon"
    #: Whether a public geologic-unit raster is configured for this body.
    has_geology_source: bool = True
    #: Whether a public crater catalogue is configured for this body.
    has_crater_source: bool = True

    @property
    def biome_ids(self) -> tuple[str, ...]:
        return tuple(b.id for b in self.biomes)

    def biome_code(self, biome_id: str) -> int:
        return self.biome_ids.index(biome_id)


# ---------------------------------------------------------------------------
# Luna
# ---------------------------------------------------------------------------

#: Luna biome table (design doc §2.4).
#:
#: Craters are **not** a biome: the Moon is saturated with large craters, so
#: "inside a big crater" would swallow most of the surface (≥20 km craters
#: geometrically cover ~75% of it).  Craters are modelled purely as the
#: movement overlay in :mod:`spm.crater`.
LUNA_BIOMES: tuple[BiomeDef, ...] = (
    BiomeDef("luna.mare", "Mare", 0),
    BiomeDef("luna.lowlands", "Lowlands", 1),
    BiomeDef("luna.midlands", "Midlands", 2),
    BiomeDef("luna.highlands", "Highlands", 3),
    BiomeDef("luna.pole_north_lowlands", "North Pole Lowlands", 1),
    BiomeDef("luna.pole_north_midlands", "North Pole Midlands", 2),
    BiomeDef("luna.pole_north_highlands", "North Pole Highlands", 3),
    BiomeDef("luna.pole_south_lowlands", "South Pole Lowlands", 1),
    BiomeDef("luna.pole_south_midlands", "South Pole Midlands", 2),
    BiomeDef("luna.pole_south_highlands", "South Pole Highlands", 3),
)

MOON = BodyDef(
    key="moon",
    display_name="Moon",
    radius_km=1737.4,
    fine_resolution=6,
    region_resolution=3,
    biomes=LUNA_BIOMES,
    # USGS Unified Geologic Map of the Moon (2020), numeric raster DN codes:
    #   7  Em  Mare Unit (Eratosthenian)
    #   22 Im1 Lower Mare Unit (Imbrian)
    #   23 Im2 Upper Mare Unit (Imbrian)
    #   24 Imd Mare Dome Unit (Imbrian)
    mare_geology_dns=frozenset({7, 22, 23, 24}),
)

# ---------------------------------------------------------------------------
# Mars
# ---------------------------------------------------------------------------

#: Mars biome table.  There are no oceans; the dominant split is the
#: northern-lowlands / southern-highlands dichotomy plus polar caps.  Volcanic
#: and canyon provinces are kept as separate biomes so building restrictions
#: can distinguish them later (C2), but they are not derived from elevation.
MARS_BIOMES: tuple[BiomeDef, ...] = (
    BiomeDef("mars.lowlands", "Northern Lowlands", 0),
    BiomeDef("mars.midlands", "Midlands", 1),
    BiomeDef("mars.highlands", "Southern Highlands", 2),
    BiomeDef("mars.pole_north", "North Polar Region", 1),
    BiomeDef("mars.pole_south", "South Polar Region", 2),
    BiomeDef("mars.volcanic", "Volcanic Province", 3),
    BiomeDef("mars.canyon", "Canyon System", 1),
)

MARS = BodyDef(
    key="mars",
    display_name="Mars",
    radius_km=3389.5,
    fine_resolution=6,
    region_resolution=3,
    biomes=MARS_BIOMES,
    # MOLA supplies elevation, the USGS 15M geologic map supplies the
    # volcanic/canyon/polar province overrides, and the Robbins & Hynek
    # catalogue supplies the crater overlay.
    mare_biome_id=None,
    lowlands_biome_id="mars.lowlands",
    midlands_biome_id="mars.midlands",
    highlands_biome_id="mars.highlands",
    pole_north_biome_ids=("mars.pole_north",) * 3,
    pole_south_biome_ids=("mars.pole_south",) * 3,
    volcanic_biome_id="mars.volcanic",
    canyon_biome_id="mars.canyon",
    pole_latitude_deg=80.0,
    lowlands_max_m=-2000.0,
    midlands_max_m=1000.0,
    source_profile="mars",
    has_geology_source=True,
    has_crater_source=True,
)

# ---------------------------------------------------------------------------
# Earth
# ---------------------------------------------------------------------------

#: Earth biome table. Codes are the byte written to each fine hex record.
#: Water classes are ordinary biomes so placement/scoring can see them without
#: changing TileFormat v1; per-building water permissions decide who may build.
EARTH_BIOMES: tuple[BiomeDef, ...] = (
    BiomeDef("earth.ocean", "Ocean", 0, water_kind="ocean"),
    BiomeDef("earth.lake", "Lake", 0, water_kind="lake"),
    BiomeDef("earth.river", "River", 0, water_kind="river"),
    BiomeDef("earth.shore", "Shore", 1),
    BiomeDef("earth.plains", "Plains", 2),
    BiomeDef("earth.hills", "Hills", 3),
    BiomeDef("earth.mountains", "Mountains", 4),
    BiomeDef("earth.forest", "Forest", 2),
    BiomeDef("earth.taiga", "Taiga", 2),
    BiomeDef("earth.tundra", "Tundra", 1),
    BiomeDef("earth.pole_north", "North Pole", 1),
    BiomeDef("earth.pole_south", "South Pole", 1),
    BiomeDef("earth.volcanic", "Volcanic", 4, buildable=False),
)

EARTH = BodyDef(
    key="earth",
    display_name="Earth",
    radius_km=6371.0072,
    fine_resolution=6,
    region_resolution=3,
    biomes=EARTH_BIOMES,
    mare_biome_id=None,
    lowlands_biome_id="earth.plains",
    midlands_biome_id="earth.hills",
    highlands_biome_id="earth.mountains",
    pole_north_biome_ids=("earth.pole_north",) * 3,
    pole_south_biome_ids=("earth.pole_south",) * 3,
    volcanic_biome_id="earth.volcanic",
    canyon_biome_id=None,
    pole_latitude_deg=66.5,
    lowlands_max_m=0.0,
    midlands_max_m=800.0,
    source_profile="earth",
    has_geology_source=True,
    has_crater_source=False,
)

BODIES: dict[str, BodyDef] = {
    MOON.key: MOON,
    MARS.key: MARS,
    EARTH.key: EARTH,
}


def get_body(key: str) -> BodyDef:
    try:
        return BODIES[key.lower()]
    except KeyError as exc:  # pragma: no cover - defensive
        raise KeyError(f"unknown body {key!r}; known: {sorted(BODIES)}") from exc
