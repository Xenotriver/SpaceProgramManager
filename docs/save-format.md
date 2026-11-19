# Save format (v4)

The campaign save is a directory ("slot") containing one metadata file, one
derived summary, and one JSON shard per region that has installations. The
implementation is `Sim/Campaign/CampaignStore.cs` and
`Sim/Campaign/CampaignSlotStore.cs`; this document is the contract. The current
schema is **v4**; v1, v2 and v3 saves are read and migrated in memory, never
overwritten on load.

## Layout

```
user://saves/                        # save root (settings.json chooses it)
├─ slot1/                            # a slot; one campaign per directory
│  ├─ campaign.json                  # metadata + bake fingerprint (authoritative)
│  ├─ campaign.json.bak              # previous good metadata
│  ├─ slot.json                      # derived summary + last view (cache)
│  ├─ slot.json.bak                  # previous good summary
│  └─ regions/
│     ├─ <region-h3>.json            # sites and installations in one region
│     ├─ <region-h3>.json.bak        # previous good shard
│     └─ <region-h3>.json.corrupt    # quarantined unreadable shard
└─ slot-20261116-183740/             # campaigns started from the menu
```

Default slot id: `slot1`, kept for compatibility with pre-M2 saves. A normal
launch opens the menu instead; `--slot=<id>` or `--save=<dir>` selects a slot
directly for automation. `--fresh` resets the selected slot; `--nosave` disables
persistence.

Hex indices are stored as **decimal strings** (H3 values exceed JavaScript's safe
integer range).

## `campaign.json`

```json
{
  "format": "spm-campaign",
  "version": 4,
  "bodyKey": "moon",
  "fineResolution": 6,
  "regionResolution": 3,
  "regionCount": 41162,
  "generatedUtc": "2026-11-11T21:48:07+00:00",
  "createdUtc": "2026-11-15T10:32:08.9438921+00:00",
  "updatedUtc": "2026-11-17T09:14:00.331109+00:00",
  "gameTimeHours": 32.5,
  "credits": 9750000,
  "sciencePoints": 125.5,
  "rngSeed": 1234567890123456789,
  "rngState": 1234567890123456797,
  "stockpile": { "metals": 220, "oxygen": 487.5 },
  "unlockedTechs": ["basic_metallurgy"],
  "visitedRegions": ["589971619905011711"],
  "activeRegion": "589971619905011711",
  "contracts": {
    "active": [
      {
        "id": "9b6d0f8e-0000-4000-8000-000000000001",
        "defId": "oxygen_delivery",
        "status": "active",
        "acceptedGameHour": 32.5,
        "deadlineGameHour": 80.0,
        "progress": { "oxygen": 120 },
        "rewardClaimed": false
      }
    ],
    "completed": [],
    "failed": []
  },
  "events": {
    "pending": [
      {
        "id": "c14a7d20-0000-4000-8000-000000000002",
        "defId": "dust_storm",
        "raisedGameHour": 40.0,
        "targetRegion": "589971619905011711",
        "choices": ["shelter", "continue"]
      }
    ],
    "cooldownGameHour": 55.0,
    "log": [
      {
        "gameHour": 40.0,
        "defId": "dust_storm",
        "choice": "shelter",
        "summary": "Habitat secured"
      }
    ]
  },
  "rovers": [
    {
      "id": "6f2a1b90-0000-4000-8000-000000000003",
      "name": "Athena",
      "defId": "rover_mk1",
      "status": "enroute",
      "health": 100,
      "speedKmh": 8,
      "locationRegion": "589971619905011711",
      "locationHex": "603482350201864191",
      "destinationRegion": "592315495026958335",
      "destinationHex": "604395630023950335",
      "departureGameHour": 40.0,
      "arrivalGameHour": 46.5,
      "cargo": { "oxygen": 20 }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `format` | Always `spm-campaign` |
| `version` | Schema version; currently `4` (v1–v3 are accepted and migrated) |
| `bodyKey`, `fineResolution`, `regionResolution`, `regionCount`, `generatedUtc` | **Bake fingerprint** — must equal the loaded manifest |
| `createdUtc`, `updatedUtc` | Campaign timestamps (UTC, ISO 8601) |
| `gameTimeHours` | Authoritative in-game clock in hours; advanced in 0.25 h steps by `CampaignState.Tick` |
| `credits`, `stockpile` | Authoritative treasury and global resources; charged at placement, consumed by maintenance and changed by production |
| `sciencePoints` | Accumulated science from operational labs |
| `rngSeed`, `rngState` | SplitMix64 seed and opaque current state (unsigned JSON numbers); the state advances with each random draw and is restored exactly |
| `unlockedTechs` | Technology ids available to `CampaignState.Preview`; empty at game start |
| `visitedRegions` | Regions opened at least once (decimal H3 strings, sorted) |
| `activeRegion` | Region to reopen on launch, or omitted |
| `contracts` | Accepted/completed/failed contracts; each record carries a stable id, definition id, status, timestamps, progress counters and reward flag |
| `events` | Pending choices, event cooldown and a bounded history (last 50 entries). Event definitions stay in static registries |
| `rovers` | Campaign-wide rover fleet: identity, location, destination, travel times, health, speed and cargo. Stored here because rovers cross region shards |

Null fields are omitted (`JsonIgnoreCondition.WhenWritingNull`). Property names
are camelCase. Hex indices are decimal strings; RNG values are numeric `ulong`s.

### M5 state details

- Contract and event `status` values are lowercase enum strings. An unknown
  value falls back to `active`/`idle`; a record with no definition id is skipped
  rather than guessed. A missing or invalid record id gets a fresh GUID.
- `contracts.progress` keys are resource or counter ids defined by the contract
  registry; the save carries only delivered amounts.
- `events.pending[].choices` are the choice ids presented to the player. `log`
  is trimmed to `CampaignDefaults.MaxEventLogEntries` (50) on load and save.
- Rover `speedKmh` defaults to `CampaignDefaults.DefaultRoverSpeedKmh` (8),
  resolving the old 8 vs 10 km/h drift. Travel timestamps are authoritative
  game hours, not wall-clock times.
- IDs in these blocks are stable strings; definition data, rewards and event
  text live in static registries, not in the save.

### Migration from v1

A v1 file has no tech, RNG or per-building pause fields, and its clock/economy
were not simulated. `LoadMeta` accepts it without
changing the file and applies these defaults in memory:

- `credits`: kept when positive; set to `CampaignDefaults.StartingCredits`
  (10,000,000) when zero or negative.
- `stockpile`, `unlockedTechs`: existing entries are kept; missing starting
  resources are filled from `CampaignDefaults`.
- `rngSeed`: derived deterministically from the stored `createdUtc`; `rngState`
  starts equal to the seed. It is **not** derived from the load time, so repeated
  loads replay the same sequence.
- `gameTimeHours`, `visitedRegions`, `activeRegion`, fingerprint: preserved.

The next write (`SaveMeta`) stores v4. A v2 or v3 metadata file has the same
core fields as v4, so it is read unchanged and rewritten as v4 on the next save.
v3 files have no M5 blocks; contracts, events and rovers start empty. These paths
are covered by `CampaignStoreTests.Meta_MigratesV1SaveToCurrentDefaults`,
`Meta_MigratesV3SaveToV4Defaults` and the v4 round-trip tests.

## `slot.json`

A **derived cache plus UI state**, never authoritative. It exists so the slot list
can show a name, counts, timestamps and the last camera without opening each
campaign. Deleting it is always safe; the next `CampaignSlotStore.List` rebuilds it
from `campaign.json` and `regions/*.json`.

```json
{
  "format": "spm-slot",
  "version": 1,
  "name": "Campaign 2",
  "createdUtc": "2026-11-16T18:30:00+00:00",
  "updatedUtc": "2026-11-16T18:37:41.331109+00:00",
  "lastPlayedUtc": "2026-11-16T18:37:40.8982242+00:00",
  "buildingCount": 3,
  "visitedRegionCount": 1,
  "bodyKey": "moon",
  "credits": 0,
  "view": {
    "mode": "management",
    "globeYawDegrees": -90,
    "globePitchDegrees": -14.3,
    "globeZoom": 1,
    "colonyRotationSteps": 0,
    "overlay": "terrain",
    "selectedHexOrdinal": 0
  }
}
```

| Field | Meaning |
|---|---|
| `format` / `version` | Always `spm-slot` / `1`; any other value makes the cache unreadable (it is rebuilt, not trusted) |
| `name` | Display name; falls back to the directory name (`slot1` → “Slot 1”) |
| `createdUtc`, `updatedUtc` | Mirrors `campaign.json` |
| `lastPlayedUtc` | Drives last-played ordering and Continue |
| `buildingCount`, `visitedRegionCount` | Counts used by the slot list |
| `bodyKey`, `credits` | List details |
| `view` | Camera/mode snapshot restored by Continue; `selectedHexOrdinal` is the fine hex whose site was open, or null outside site view; `overlay` may be `color` after M4.5 but is UI-only |

**Trust rule.** `slot.json` is used as-is only while its `updatedUtc` equals the
one in `campaign.json`. Otherwise the store recounts the region shards and
rewrites it. A corrupt or missing summary never blocks opening a slot.

The summary is not part of the campaign schema: `campaign.json` is v4 (v1/v2/v3
saves are migrated on load), an older runtime ignores `slot.json`, and a newer
runtime rebuilds it from the campaign. Its own `spm-slot` version stays `1`; site
selection is an optional UI field. The M4.5 `color` overlay value is likewise
additive UI state: when a valid imagery sidecar exists the game defaults to true
colour for every slot, so saved overlay strings do not gate or migrate imagery.

`data/<body>/color.bin` and `imagery.json` are **not** part of any slot or bake
contract. They are presentation-only sidecars validated independently of
`BakeFingerprint`; a missing or invalid sidecar never changes a save and never
makes a save incompatible.

## `regions/<h3>.json`

```json
{
  "format": "spm-campaign",
  "version": 4,
  "region": "589971619905011711",
  "sites": [
    {
      "parentHex": "603482350201864191",
      "installations": [
        {
          "id": "81831615-f3d8-4346-b406-9f30310590d9",
          "defId": "air_reclaimer",
          "localCenter": { "q": 0, "r": 0 },
          "progress": 1,
          "health": 100,
          "status": "Operational",
          "paused": false,
          "statusReason": "",
          "storedResources": {},
          "constructionStartTime": 0.5,
          "constructionCompleteTime": 18.5,
          "failure": {
            "cause": "dust_storm",
            "severity": 0.4,
            "startedGameHour": 40.0,
            "repairStartGameHour": 44.0,
            "repairCompleteGameHour": null
          }
        },
        {
          "id": "0f43ceb5-505d-4140-9069-61df1204863a",
          "defId": "solar_array",
          "localCenter": { "q": 1, "r": 0 },
          "progress": 1,
          "health": 100,
          "status": "Operational",
          "paused": false,
          "statusReason": "",
          "storedResources": {},
          "constructionStartTime": 0.5,
          "constructionCompleteTime": 12.5
        }
      ]
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `region` | Must match the file name and the requested region index |
| `sites[]` | One entry per occupied fine hex; sites appear in first-placement order |
| `sites[].parentHex` | H3 index of the fine hex containing the site (decimal string) |
| `installations[]` | Installations inside the site, in placement order |
| `installations[].id` | Stable GUID; regenerated only when missing/invalid |
| `installations[].defId` | Building definition id |
| `installations[].localCenter` | Axial `{ "q": …, "r": … }` cell at the installation's centre |
| `progress`, `health` | 0..1 construction progress, 0..100 health |
| `status` | `Planned`, `UnderConstruction`, `Operational`, `Idle`, `Damaged`, `Destroyed` |
| `paused` | True when the player froze construction/production; wrong values become `false` in v1 shards |
| `statusReason` | Human-readable reason for `Idle`/`Paused` (e.g. `power deficit: needs 5 kW, 0 kW available`); omitted when empty |
| `storedResources` | Per-installation resource buffer |
| `constructionStartTime`, `constructionCompleteTime` | Game hours when construction began and finished; the latter is omitted until complete |
| `state` | Optional flat string/number/bool bag for future per-installation data |
| `failure` | Optional typed M5 failure/repair record: `cause`, `severity` (0..1), `startedGameHour`, optional `repairStartGameHour` and `repairCompleteGameHour`; omitted while healthy |

`statusReason`, `constructionCompleteTime`, `state` and `failure` are omitted when
null/empty. **The local footprint is never stored**: it is rebuilt from the
definition and `localCenter` on load. v3/v4 shards have no `buildings` or
`centerHex` field; those only appear in legacy shards.

### v1/v2 → v4 shard migration

A legacy v1 or v2 shard holds a flat `buildings` array whose records use
`centerHex` instead of `parentHex` / `localCenter`. `LoadRegion` converts each
distinct centre hex into one site with that centre's fine hex as `parentHex` and
`localCenter = { "q": 0, "r": 0 }`. All other stored fields are copied verbatim;
v1 records also receive the M1 defaults (`paused = false`, empty `statusReason`).
The old no-overlap rule guarantees distinct centre hexes, so no conflict
resolution is needed. Conversion is read-only until the next save, which writes
v4. A v3 shard already has the v4 site shape; it loads directly and simply has no
`failure` records. Tests cover v2 → v4, the v1 → v2 → v4 chain, v3 migration and
refusal of unknown versions.

## Invariants

1. **Local footprints are derived, never trusted.** On load,
   `CampaignState.InstallRegion` rebuilds each footprint from its definition and
   local centre, then drops records whose parent hex is outside the region, whose
   definition is unknown, whose footprint leaves the 37-cell disk, or which
   overlaps another installation **in the same site**. Each drop is reported as a
   warning. Different sites are independent even when they use the same local
   cell.
2. **GUIDs are stable.** A valid stored id is preserved across saves; invalid or
   missing ids get a new GUID and the installation is kept.
3. **Unknown definition ids stay readable but do not enter live state.**
   `LoadRegion` keeps the record with a warning; `InstallRegion` drops it, so the
   next save of that region omits it. This is the same behaviour as v2.
4. **Only primitive `state` values survive.** Strings, numbers and booleans are
   kept; objects/arrays are dropped with a warning.
5. **Failure/repair is typed.** M5 gameplay writes `failure`, not gameplay keys
   inside the primitive `state` bag. Old shards without the field are healthy.
6. **Region index must match the file name**, otherwise the shard is rejected.
7. **`slot.json` is never authoritative.** It only mirrors campaign metadata and
   view state; deleting it must not lose an installation, a credit, a contract, a
   rover or a visited region.

## Atomic writes and backups

Every write goes through `WriteAtomic`:

1. Serialize UTF-8 **without BOM**.
2. Write `<path>.tmp`.
3. If `<path>` exists, copy it to `<path>.bak` (overwrite).
4. `File.Move(tmp, path, overwrite: true)`.

Deleting a region removes both the shard and its `.bak`. `CampaignService` deletes
a shard when its last installation is removed, so empty regions are not stored as
an empty file.

## Load policy

| Situation | Behaviour |
|---|---|
| Slot has no `campaign.json` | Not listed as a playable slot |
| Slot has no `slot.json` | Rebuilt from `campaign.json` + region shards on the next list |
| `slot.json` has a stale `updatedUtc` | Rebuilt; its cached counts are ignored |
| `slot.json` is corrupt or has another format/version | Ignored and overwritten when the slot is listed |
| Metadata invalid JSON / wrong format / version outside 1–4 | Listed as **unreadable**; opening is refused and the file is never overwritten |
| Metadata version 1 | Migrated in memory with starting defaults; the file is unchanged until the next save |
| Metadata version 2 or 3 | Same core fields as v4; M5 blocks start empty and the file is rewritten as v4 on the next save |
| Region shard version 1 or 2 | Converted to one site per legacy `centerHex` at local `(0,0)`; v1 also gets `paused = false` and empty `statusReason`; file unchanged until the next save |
| Region shard version 3 | Read directly; site shape is unchanged, `failure` is absent, footprints rebuilt from definitions |
| Region shard version 4 | Read directly; typed failure records restored when present |
| Fingerprint mismatch | Listed as **incompatible** with both fingerprints; opening is refused and the files are kept |
| Opening a readable slot | Load metadata, then `slot.json` (or a recount), then shards on demand |
| Missing region shard | Empty region (not an error) |
| Corrupt region shard | Move to `.corrupt`, install an empty region, warn |
| Shard region index mismatch | Treated as corrupt (quarantined) |
| `--fresh` | Delete every file in `regions/`, rewrite metadata and summary |

Metadata and the summary are saved on: campaign creation, active-region change,
every region mutation, pausing the clock, the 5-second autosave, manual save
(`F5`), and `Flush()` on exit. Region shards are saved immediately on mutation.
`slot.json` also records the view snapshot taken when the scene closes or the
player returns to the menu. Only regions installed in memory are ticked, so a
long pause in one region does not silently advance distant colonies.

## Compatibility policy

- `version` is bounded: the runtime accepts `1` through `4`; older versions are
  migrated in memory and rewritten only on an explicit save. Unknown or future
  versions are refused, not guessed, and the file is never overwritten.
- Any schema change requires: bump `Version`, add a migration path from every
  still-supported older version, add a `CampaignStoreTests` fixture for each
  migration, and update this document. The v4 shard path keeps the v1 → v2 → v3
  chain working, including the bake fingerprint gate.
- A v4 save is deliberately unreadable by the v3 runtime: it refuses the future
  version and never overwrites the file. M5 content must not be silently dropped
  by an older build.
- Unknown **fields** are tolerated by `System.Text.Json` (ignored), which allows
  additive changes in a future version only if old runtimes can safely ignore
  them. Do not rely on that for semantic changes.
- The bake fingerprint is part of the contract: `bodyKey`, `generatedUtc`, region
  count and resolutions must all match the loaded manifest. Regenerating
  `data/<body>` with different values, or opening a save under another body,
  invalidates it by design. A future migration may remap buildings by H3 index if
  the bake is compatible; that does not exist yet.

`slot.json` has its own `format`/`version` (`spm-slot` / `1`). Bumping it does not
touch `campaign.json`: an older runtime ignores the file and a newer runtime
rebuilds it. Only changes to the **campaign** schema require a migration.
