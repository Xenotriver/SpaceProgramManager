# Save format (v1)

The campaign save is a directory ("slot") containing one metadata file, one
derived summary, and one JSON shard per region that has buildings. The
implementation is `Sim/Campaign/CampaignStore.cs` and
`Sim/Campaign/CampaignSlotStore.cs`; this document is the contract.

## Layout

```
user://saves/                        # save root (settings.json chooses it)
├─ slot1/                            # a slot; one campaign per directory
│  ├─ campaign.json                  # metadata + bake fingerprint (authoritative)
│  ├─ campaign.json.bak              # previous good metadata
│  ├─ slot.json                      # derived summary + last view (cache)
│  ├─ slot.json.bak                  # previous good summary
│  └─ regions/
│     ├─ <region-h3>.json            # buildings in one region
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
  "version": 1,
  "bodyKey": "moon",
  "fineResolution": 6,
  "regionResolution": 3,
  "regionCount": 41162,
  "generatedUtc": "2026-11-11T21:48:07+00:00",
  "createdUtc": "2026-11-15T10:32:08.9438921+00:00",
  "updatedUtc": "2026-11-15T10:32:08.9438921+00:00",
  "gameTimeHours": 0,
  "credits": 0,
  "stockpile": {},
  "visitedRegions": ["589971619905011711"],
  "activeRegion": "589971619905011711"
}
```

| Field | Meaning |
|---|---|
| `format` | Always `spm-campaign` |
| `version` | Schema version; currently `1` |
| `bodyKey`, `fineResolution`, `regionResolution`, `regionCount`, `generatedUtc` | **Bake fingerprint** — must equal the loaded manifest |
| `createdUtc`, `updatedUtc` | Campaign timestamps (UTC, ISO 8601) |
| `gameTimeHours` | In-game clock; persisted but not advanced yet |
| `credits`, `stockpile` | Treasury and global resources; persisted but not simulated yet |
| `visitedRegions` | Regions opened at least once (decimal H3 strings, sorted) |
| `activeRegion` | Region to reopen on launch, or omitted |

Null fields are omitted (`JsonIgnoreCondition.WhenWritingNull`). Property names
are camelCase.

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
    "overlay": "terrain"
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
| `view` | Camera/mode snapshot restored by Continue |

**Trust rule.** `slot.json` is used as-is only while its `updatedUtc` equals the
one in `campaign.json`. Otherwise the store recounts the region shards and
rewrites it. A corrupt or missing summary never blocks opening a slot.

The summary is not part of the save schema: `campaign.json` stays v1, an older
runtime ignores `slot.json`, and a newer runtime rebuilds it from the campaign.

## `regions/<h3>.json`

```json
{
  "format": "spm-campaign",
  "version": 1,
  "region": "589971619905011711",
  "buildings": [
    {
      "id": "5ca5eadc-8ffc-4187-90e3-080a5ef8eb85",
      "defId": "rtg_generator",
      "centerHex": "603482367650168831",
      "progress": 0,
      "health": 100,
      "status": "Planned",
      "storedResources": {},
      "constructionStartTime": 0
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `region` | Must match the file name and the requested region index |
| `buildings[].id` | Stable GUID; regenerated only when missing/invalid |
| `buildings[].defId` | Building definition id |
| `buildings[].centerHex` | Centre cell; **the footprint is not stored** |
| `progress`, `health` | 0..1 construction progress, 0..100 health |
| `status` | `Planned`, `UnderConstruction`, `Operational`, `Idle`, `Damaged`, `Destroyed` |
| `storedResources` | Per-building resource buffer |
| `constructionStartTime`, `constructionCompleteTime` | Game hours; currently only written |
| `state` | Optional flat string/number/bool bag for future per-building data |

`constructionCompleteTime` and `state` are omitted when null/empty.

## Invariants

1. **Footprints are derived, never trusted.** On load, `CampaignState.InstallRegion`
   rebuilds each footprint from its definition and centre hex, then drops records
   that are unknown, out of region, overlapping, or clipped by the boundary. Each
   drop is reported as a warning.
2. **GUIDs are stable.** A valid stored id is preserved across saves; invalid or
   missing ids get a new GUID and the building is kept.
3. **Unknown definition ids are kept on disk** (so a later build with the
   definition can restore them) but are not installed into the live region.
4. **Only primitive `state` values survive.** Strings, numbers and booleans are
   kept; objects/arrays are dropped with a warning.
5. **Region index must match the file name**, otherwise the shard is rejected.
6. **`slot.json` is never authoritative.** It only mirrors campaign metadata and
   view state; deleting it must not lose a building, a credit or a visited region.

## Atomic writes and backups

Every write goes through `WriteAtomic`:

1. Serialize UTF-8 **without BOM**.
2. Write `<path>.tmp`.
3. If `<path>` exists, copy it to `<path>.bak` (overwrite).
4. `File.Move(tmp, path, overwrite: true)`.

Deleting a region removes both the shard and its `.bak`. `CampaignService` deletes
a shard when its last building is removed, so empty regions are not stored as an
empty file.

## Load policy

| Situation | Behaviour |
|---|---|
| Slot has no `campaign.json` | Not listed as a playable slot |
| Slot has no `slot.json` | Rebuilt from `campaign.json` + region shards on the next list |
| `slot.json` has a stale `updatedUtc` | Rebuilt; its cached counts are ignored |
| `slot.json` is corrupt or has another format/version | Ignored and overwritten when the slot is listed |
| Metadata invalid JSON / wrong format / unsupported version | Listed as **unreadable**; opening is refused and the file is never overwritten |
| Fingerprint mismatch | Listed as **incompatible** with both fingerprints; opening is refused and the files are kept |
| Opening a readable slot | Load metadata, then `slot.json` (or a recount), then shards on demand |
| Missing region shard | Empty region (not an error) |
| Corrupt region shard | Move to `.corrupt`, install an empty region, warn |
| Shard region index mismatch | Treated as corrupt (quarantined) |
| `--fresh` | Delete every file in `regions/`, rewrite metadata and summary |

Metadata and the summary are saved on: campaign creation, active-region change,
every region mutation, manual save (`F5`), and `Flush()` on exit. Region shards
are saved immediately on mutation. `slot.json` also records the view snapshot
taken when the scene closes or the player returns to the menu.

## Compatibility policy

- `version` is exact: the runtime accepts only `1`. Unknown versions are refused,
  not guessed.
- Any schema change requires: bump `Version`, add a migration path, add a
  `CampaignStoreTests` case that loads a v1 fixture, and update this document.
- Unknown **fields** are tolerated by `System.Text.Json` (ignored), which allows
  additive changes in a future version only if old runtimes can safely ignore
  them. Do not rely on that for semantic changes.
- The bake fingerprint is part of the contract: regenerating `data/moon` with a
  different `generatedUtc`, region count, or resolutions invalidates old saves by
  design. A future migration may remap buildings by H3 index if the bake is
  compatible; that does not exist yet.

`slot.json` has its own `format`/`version` (`spm-slot` / `1`). Bumping it does not
touch `campaign.json`: an older runtime ignores the file and a newer runtime
rebuilds it. Only changes to the **campaign** schema require a migration.
