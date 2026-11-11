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

BODIES: dict[str, BodyDef] = {MOON.key: MOON}


def get_body(key: str) -> BodyDef:
    try:
        return BODIES[key.lower()]
    except KeyError as exc:  # pragma: no cover - defensive
        raise KeyError(f"unknown body {key!r}; known: {sorted(BODIES)}") from exc
