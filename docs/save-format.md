# Save format (v1)

The campaign save is a directory ("slot") containing one metadata file and one
JSON shard per region that has buildings. The implementation is
`Sim/Campaign/CampaignStore.cs`; this document is the contract.

## Layout

```
<slot>/
├─ campaign.json                     # metadata + bake fingerprint
├─ campaign.json.bak                 # previous good metadata
└─ regions/
   ├─ <region-h3>.json               # buildings in one region
   ├─ <region-h3>.json.bak           # previous good shard
   └─ <region-h3>.json.corrupt       # quarantined unreadable shard
```

Default slot: `user://saves/slot1` (Godot's per-user data directory). Override
with `--save=<dir>`; `--fresh` resets it; `--nosave` disables persistence.

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
| Slot has no `campaign.json` | New campaign: write metadata, status `save · new campaign` |
| Metadata invalid JSON / wrong format / unsupported version | **Persistence disabled for the run**; status `save · disabled · incompatible`; the file is never overwritten |
| Fingerprint mismatch | Same as above; message names both fingerprints |
| Missing region shard | Empty region (not an error) |
| Corrupt region shard | Move to `.corrupt`, install an empty region, warn |
| Shard region index mismatch | Treated as corrupt (quarantined) |
| `--fresh` | Delete every file in `regions/`, rewrite metadata |

Metadata is saved on: campaign creation, active-region change, every region
mutation, and `Flush()` on exit. Region shards are saved immediately on mutation.

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
