using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using H3;
using Sim.Buildings;
using Sim.Site;

namespace Sim.Campaign;

/// <summary>
/// Engine-free persistence for a campaign: <c>campaign.json</c> plus one JSON
/// shard per visited region under <c>regions/</c>.
///
/// Writes are atomic (temp file + replace) and keep the previous good file as
/// <c>.bak</c>. The store is deliberately dumb: it moves DTOs to and from disk
/// and leaves footprint rebuilding and game-rule validation to
/// <see cref="CampaignState.InstallRegion"/>.
/// </summary>
public sealed class CampaignStore
{
    /// <summary>Format id written into every save file.</summary>
    public const string FormatId = "spm-campaign";

    /// <summary>Current save schema version (v4 adds contracts, events and rovers).</summary>
    public const int Version = 4;

    /// <summary>Oldest schema that can be read and migrated in place.</summary>
    public const int OldestSupportedVersion = 1;

    /// <summary>Format id written into <c>slot.json</c>.</summary>
    public const string SlotFormatId = "spm-slot";

    /// <summary>Current <c>slot.json</c> schema version.</summary>
    public const int SlotVersion = 1;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CampaignStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Directory holding the campaign and its region shards.</summary>
    public string RootDirectory { get; }

    /// <summary>Path of the campaign metadata file.</summary>
    public string MetaPath => Path.Combine(RootDirectory, "campaign.json");

    /// <summary>
    /// Path of the derived slot summary. Safe to delete; it is rebuilt from the
    /// campaign and its region shards.
    /// </summary>
    public string SummaryPath => Path.Combine(RootDirectory, "slot.json");

    /// <summary>True when a campaign already exists at this location.</summary>
    public bool Exists => File.Exists(MetaPath);

    /// <summary>Path of one region's shard.</summary>
    public string RegionPath(ulong regionIndex)
        => Path.Combine(RootDirectory, "regions", $"{regionIndex.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>
    /// Region indices that have a shard on disk. Used for globe markers without
    /// loading every region into memory.
    /// </summary>
    public IReadOnlyCollection<ulong> ListRegionIndices()
    {
        var directory = Path.Combine(RootDirectory, "regions");
        if (!Directory.Exists(directory))
            return Array.Empty<ulong>();

        var indices = new List<ulong>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                indices.Add(index);
        }

        return indices;
    }

    /// <summary>Writes campaign metadata, refusing to silently change the bake it belongs to.</summary>
    public void SaveMeta(CampaignMeta meta, BakeFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var dto = new CampaignDto
        {
            Format = FormatId,
            Version = Version,
            BodyKey = fingerprint.BodyKey,
            FineResolution = fingerprint.FineResolution,
            RegionResolution = fingerprint.RegionResolution,
            RegionCount = fingerprint.RegionCount,
            GeneratedUtc = fingerprint.GeneratedUtc,
            CreatedUtc = meta.CreatedUtc,
            UpdatedUtc = meta.UpdatedUtc,
            GameTimeHours = meta.GameTimeHours,
            Credits = meta.Credits,
            SciencePoints = meta.SciencePoints,
            RngSeed = meta.RngSeed,
            RngState = meta.RngState,
            UnlockedTechs = meta.UnlockedTechs.OrderBy(tech => tech, StringComparer.Ordinal).ToList(),
            Stockpile = new Dictionary<string, double>(meta.Stockpile, StringComparer.Ordinal),
            VisitedRegions = meta.VisitedRegions
                .OrderBy(index => index)
                .Select(HexString)
                .ToList(),
            ActiveRegion = meta.ActiveRegionIndex is { } active ? HexString(active) : null,
            Contracts = ToContractBlock(meta),
            Events = ToEventBlock(meta),
            Rovers = meta.Rovers
                .OrderBy(rover => rover.Id, StringComparer.Ordinal)
                .Select(ToDto)
                .ToList(),
        };

        WriteAtomic(MetaPath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>
    /// Reads campaign metadata and checks the format, schema version and bake
    /// fingerprint. Throws <see cref="InvalidDataException"/> when a save cannot
    /// be safely applied to the loaded bake.
    /// </summary>
    public CampaignMeta LoadMeta(BakeFingerprint expected)
    {
        var path = MetaPath;
        if (!File.Exists(path))
            throw new FileNotFoundException("Campaign file not found.", path);

        CampaignDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CampaignDto>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: invalid JSON: {ex.Message}", ex);
        }

        if (dto is null)
            throw new InvalidDataException($"{path}: empty campaign file.");

        EnsureSupported(dto.Format, dto.Version, path);

        var stored = new BakeFingerprint(
            dto.BodyKey,
            dto.FineResolution,
            dto.RegionResolution,
            dto.RegionCount,
            dto.GeneratedUtc);

        if (stored != expected)
            throw new InvalidDataException(
                $"{path}: save was created for [{stored}], but the loaded bake is [{expected}].");

        var meta = new CampaignMeta
        {
            CreatedUtc = dto.CreatedUtc,
            UpdatedUtc = dto.UpdatedUtc,
            GameTimeHours = dto.GameTimeHours,
            Credits = dto.Credits,
            SciencePoints = dto.SciencePoints,
            ActiveRegionIndex = TryParseHex(dto.ActiveRegion, out var active) ? active : null,
        };

        // The constructor seeds starting stockpile/tech; a stored campaign is
        // authoritative, so clear before applying what it actually has.
        meta.Stockpile.Clear();
        meta.UnlockedTechs.Clear();

        foreach (var (resource, amount) in dto.Stockpile)
            meta.Stockpile[resource] = amount;

        foreach (var tech in dto.UnlockedTechs)
        {
            if (!string.IsNullOrWhiteSpace(tech))
                meta.UnlockedTechs.Add(tech);
        }

        foreach (var text in dto.VisitedRegions)
        {
            if (TryParseHex(text, out var index))
                meta.VisitedRegions.Add(index);
        }

        if (dto.Version == 1)
        {
            // v1 had no economy, tech or RNG. Seed from the stored creation time
            // (not the CampaignMeta constructor's clock), then give the migrated
            // save the same starting means as a new campaign.
            meta.RngSeed = CampaignDefaults.DeterministicSeed(meta.CreatedUtc);
            meta.RngState = meta.RngSeed;
            CampaignDefaults.Apply(meta);
        }
        else
        {
            meta.RngSeed = dto.RngSeed == 0
                ? CampaignDefaults.DeterministicSeed(meta.CreatedUtc)
                : dto.RngSeed;
            meta.RngState = dto.RngState == 0 ? meta.RngSeed : dto.RngState;
        }

        ApplyM5Blocks(dto, meta);
        return meta;
    }

    /// <summary>
    /// Reads the campaign header without applying the save. Lets the slot list
    /// show an incompatible or unreadable slot without ever opening it.
    /// </summary>
    public bool TryReadHeader(out CampaignHeader? header, out string? problem)
    {
        header = null;
        problem = null;

        if (!File.Exists(MetaPath))
        {
            problem = "campaign.json is missing";
            return false;
        }

        CampaignDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CampaignDto>(
                File.ReadAllText(MetaPath, Encoding.UTF8), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }

        if (dto is null)
        {
            problem = "campaign.json is empty";
            return false;
        }

        try
        {
            EnsureSupported(dto.Format, dto.Version, MetaPath);
        }
        catch (InvalidDataException ex)
        {
            problem = ex.Message;
            return false;
        }

        header = new CampaignHeader(
            new BakeFingerprint(
                dto.BodyKey,
                dto.FineResolution,
                dto.RegionResolution,
                dto.RegionCount,
                dto.GeneratedUtc),
            dto.CreatedUtc,
            dto.UpdatedUtc,
            dto.Credits,
            dto.VisitedRegions.Count,
            TryParseHex(dto.ActiveRegion, out var active) ? (ulong)active : null);

        return true;
    }

    /// <summary>
    /// Writes the derived slot summary. This file is a cache plus UI state; the
    /// campaign and its shards remain the source of truth.
    /// </summary>
    public void SaveSummary(SlotSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var dto = new SlotDto
        {
            Name = summary.Name,
            CreatedUtc = summary.CreatedUtc,
            UpdatedUtc = summary.UpdatedUtc,
            LastPlayedUtc = summary.LastPlayedUtc,
            BuildingCount = summary.BuildingCount,
            VisitedRegionCount = summary.VisitedRegionCount,
            BodyKey = summary.BodyKey,
            Credits = summary.Credits,
            View = new SlotViewDto
            {
                Mode = summary.View.Mode,
                GlobeYawDegrees = summary.View.GlobeYawDegrees,
                GlobePitchDegrees = summary.View.GlobePitchDegrees,
                GlobeZoom = summary.View.GlobeZoom,
                ColonyRotationSteps = summary.View.ColonyRotationSteps,
                Overlay = summary.View.Overlay,
                SelectedHexOrdinal = summary.View.SelectedHexOrdinal,
            },
        };

        WriteAtomic(SummaryPath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>Reads <c>slot.json</c>, returning false when it is missing or invalid.</summary>
    public bool TryReadSummary(out SlotSummary? summary)
    {
        summary = null;

        if (!File.Exists(SummaryPath))
            return false;

        SlotDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SlotDto>(
                File.ReadAllText(SummaryPath, Encoding.UTF8), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (dto is null
            || !string.Equals(dto.Format, SlotFormatId, StringComparison.Ordinal)
            || dto.Version != SlotVersion)
            return false;

        var id = Path.GetFileName(RootDirectory);
        summary = new SlotSummary
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name,
            Path = RootDirectory,
            CreatedUtc = dto.CreatedUtc,
            UpdatedUtc = dto.UpdatedUtc,
            LastPlayedUtc = dto.LastPlayedUtc,
            BuildingCount = dto.BuildingCount,
            VisitedRegionCount = dto.VisitedRegionCount,
            BodyKey = dto.BodyKey,
            Credits = dto.Credits,
            View = dto.View is null ? new SlotViewState() : new SlotViewState
            {
                Mode = dto.View.Mode,
                GlobeYawDegrees = dto.View.GlobeYawDegrees,
                GlobePitchDegrees = dto.View.GlobePitchDegrees,
                GlobeZoom = dto.View.GlobeZoom,
                ColonyRotationSteps = dto.View.ColonyRotationSteps,
                Overlay = dto.View.Overlay,
                SelectedHexOrdinal = dto.View.SelectedHexOrdinal,
            },
        };

        return true;
    }

    /// <summary>
    /// Counts region shards and stored buildings. Corrupt shards are counted but
    /// do not throw; the caller reports them.
    /// </summary>
    public StoredCounts CountStoredBuildings()
    {
        var directory = Path.Combine(RootDirectory, "regions");
        if (!Directory.Exists(directory))
            return new StoredCounts(0, 0, 0);

        var regions = 0;
        var buildings = 0;
        var unreadable = 0;

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!path.EndsWith(".json", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                regions++;

                if (document.RootElement.TryGetProperty("sites", out var sites)
                    && sites.ValueKind == JsonValueKind.Array)
                {
                    foreach (var site in sites.EnumerateArray())
                    {
                        if (site.TryGetProperty("installations", out var installations)
                            && installations.ValueKind == JsonValueKind.Array)
                            buildings += installations.GetArrayLength();
                    }
                }
                else if (document.RootElement.TryGetProperty("buildings", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    buildings += list.GetArrayLength();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        return new StoredCounts(regions, buildings, unreadable);
    }

    /// <summary>
    /// Reads one region shard. A missing shard is an empty region, not an error;
    /// a corrupt shard throws so the caller can report it instead of overwriting.
    /// </summary>
    public RegionLoadResult LoadRegion(ulong regionIndex)
    {
        var path = RegionPath(regionIndex);
        if (!File.Exists(path))
            return RegionLoadResult.Empty(regionIndex);

        RegionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RegionDto>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: invalid JSON: {ex.Message}", ex);
        }

        if (dto is null)
            throw new InvalidDataException($"{path}: empty region file.");

        EnsureSupported(dto.Format, dto.Version, path);

        if (!TryParseHex(dto.Region, out var storedRegion) || (ulong)storedRegion != regionIndex)
            throw new InvalidDataException($"{path}: region index does not match the file name.");

        var warnings = new List<string>();
        var buildings = new List<BuildingInstance>();

        if (dto.Version is 1 or 2)
        {
            // Legacy shards hold one flat building per centre hex. The old
            // no-overlap rule makes those centres distinct, so each becomes its
            // own site with the installation at the local centre.
            foreach (var stored in dto.Buildings ?? new List<LegacyBuildingDto>())
            {
                if (!TryParseHex(stored.CenterHex, out var parentHex))
                {
                    warnings.Add($"{stored.DefId}: invalid center hex '{stored.CenterHex}'");
                    continue;
                }

                if (ToInstance(stored, parentHex, LocalGrid.Center, warnings) is { } instance)
                    buildings.Add(instance);
            }
        }
        else
        {
            // A v3/v4 shard must declare its sites explicitly. A missing `sites`
            // field or a legacy `buildings` field means this file was not written
            // by the current schema. Loading it as an empty region would drop its
            // installations on the next save, so refuse it and let the caller
            // quarantine the shard instead of silently overwriting it.
            if (dto.Sites is null)
                throw new InvalidDataException($"{path}: v3/v4 shard is missing 'sites'.");

            if (dto.Buildings is { Count: > 0 })
                throw new InvalidDataException($"{path}: v3/v4 shard contains legacy 'buildings'.");

            foreach (var site in dto.Sites)
            {
                if (!TryParseHex(site.ParentHex, out var parentHex))
                {
                    warnings.Add($"site: invalid parent hex '{site.ParentHex}'");
                    continue;
                }

                if (site.Installations is null)
                {
                    warnings.Add($"site {site.ParentHex}: missing installations treated as empty");
                    continue;
                }

                foreach (var stored in site.Installations)
                {
                    var localCenter = stored.LocalCenter is null
                        ? LocalGrid.Center
                        : new LocalCell(stored.LocalCenter.Q, stored.LocalCenter.R);

                    if (!LocalGrid.IsInside(localCenter))
                    {
                        warnings.Add($"{stored.DefId}: local centre {localCenter} is outside the site grid");
                        continue;
                    }

                    if (ToInstance(stored, parentHex, localCenter, warnings) is { } instance)
                        buildings.Add(instance);
                }
            }
        }

        return new RegionLoadResult(regionIndex, buildings, warnings);
    }

    /// <summary>Maps a stored record onto a live instance; derived footprints stay empty.</summary>
    private static BuildingInstance? ToInstance(
        BuildingFieldsDto stored,
        H3Index parentHex,
        LocalCell localCenter,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(stored.DefId))
        {
            warnings.Add("building without a definition id");
            return null;
        }

        var state = new Dictionary<string, object>(StringComparer.Ordinal);
        if (stored.State is not null)
        {
            foreach (var (key, value) in stored.State)
            {
                var primitive = ToPrimitive(value);
                if (primitive is null)
                {
                    warnings.Add($"{stored.DefId}: dropped state '{key}' (unsupported value)");
                    continue;
                }

                state[key] = primitive;
            }
        }

        return new BuildingInstance
        {
            Id = Guid.TryParse(stored.Id, out var id) && id != Guid.Empty ? id : Guid.NewGuid(),
            DefId = stored.DefId,
            ParentHex = parentHex,
            LocalCenter = localCenter,
            LocalFootprint = Array.Empty<LocalCell>(),
            Progress = stored.Progress,
            Health = stored.Health,
            Status = Enum.TryParse<BuildingStatus>(stored.Status, ignoreCase: true, out var status)
                ? status
                : BuildingStatus.Planned,
            Paused = stored.Paused,
            StatusReason = stored.StatusReason ?? string.Empty,
            StoredResources = new Dictionary<string, double>(
                stored.StoredResources ?? new Dictionary<string, double>(),
                StringComparer.Ordinal),
            ConstructionStartTime = stored.ConstructionStartTime,
            ConstructionCompleteTime = stored.ConstructionCompleteTime,
            State = state,
            Failure = ToFailure(stored.Failure),
        };
    }

    /// <summary>Writes one region shard atomically.</summary>
    public void SaveRegion(ulong regionIndex, IReadOnlyList<BuildingInstance> buildings)
    {
        ArgumentNullException.ThrowIfNull(buildings);

        // Group by parent hex in first-appearance order. Installation order inside
        // each site is preserved, so a site round-trip keeps placement order.
        var sites = new List<SiteDto>();
        var byParent = new Dictionary<ulong, SiteDto>();
        foreach (var building in buildings)
        {
            var key = (ulong)building.ParentHex;
            if (!byParent.TryGetValue(key, out var site))
            {
                site = new SiteDto
                {
                    ParentHex = HexString(building.ParentHex),
                    Installations = new List<BuildingDto>(),
                };
                byParent[key] = site;
                sites.Add(site);
            }

            site.Installations.Add(ToDto(building));
        }

        var dto = new RegionDto
        {
            Format = FormatId,
            Version = Version,
            Region = HexString(regionIndex),
            Sites = sites,
        };

        WriteAtomic(RegionPath(regionIndex), JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>Deletes a region shard (and its backup), if present.</summary>
    public void DeleteRegion(ulong regionIndex)
    {
        var path = RegionPath(regionIndex);
        var backup = path + ".bak";

        // A fresh slot has no regions/ directory yet. Deleting a never-written
        // shard must be a no-op, otherwise the first autosave of an empty region
        // fails with DirectoryNotFoundException.
        if (!File.Exists(path) && !File.Exists(backup))
            return;

        File.Delete(path);
        File.Delete(backup);
    }

    /// <summary>
    /// Deletes every region shard and backup, returning how many shards were
    /// removed. Used when a slot is explicitly restarted so stale shards from the
    /// previous campaign cannot leak into the new one.
    /// </summary>
    public int DeleteAllRegions()
    {
        var directory = Path.Combine(RootDirectory, "regions");
        if (!Directory.Exists(directory))
            return 0;

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);

            if (name.EndsWith(".json", StringComparison.Ordinal))
                removed++;

            File.Delete(path);
        }

        return removed;
    }

    private static BuildingDto ToDto(BuildingInstance building)
    {
        var state = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in building.State)
        {
            var primitive = ToPrimitive(value);
            if (primitive is not null)
                state[key] = primitive;
        }

        return new BuildingDto
        {
            Id = building.Id == Guid.Empty ? null : building.Id.ToString("D"),
            DefId = building.DefId,
            LocalCenter = new LocalCellDto { Q = building.LocalCenter.Q, R = building.LocalCenter.R },
            Progress = building.Progress,
            Health = building.Health,
            Status = building.Status.ToString(),
            Paused = building.Paused,
            StatusReason = building.StatusReason.Length == 0 ? null : building.StatusReason,
            StoredResources = new Dictionary<string, double>(building.StoredResources, StringComparer.Ordinal),
            ConstructionStartTime = building.ConstructionStartTime,
            ConstructionCompleteTime = building.ConstructionCompleteTime,
            State = state.Count > 0 ? state : null,
            Failure = building.Failure is null ? null : new FailureDto
            {
                Cause = building.Failure.Cause,
                Severity = building.Failure.Severity,
                StartedGameHour = building.Failure.StartedGameHour,
                RepairStartGameHour = building.Failure.RepairStartGameHour,
                RepairCompleteGameHour = building.Failure.RepairCompleteGameHour,
            },
        };
    }

    /// <summary>
    /// Applies M5 state for every supported schema version. Older saves simply
    /// have no blocks, so their campaign starts with empty contracts/events/rovers.
    /// </summary>
    private static void ApplyM5Blocks(CampaignDto dto, CampaignMeta meta)
    {
        meta.Contracts.Clear();
        if (dto.Contracts is not null)
        {
            var stored = (dto.Contracts.Active ?? new List<ContractDto>())
                .Concat(dto.Contracts.Completed ?? new List<ContractDto>())
                .Concat(dto.Contracts.Failed ?? new List<ContractDto>());
            foreach (var contract in stored)
            {
                if (contract is not null && ToContract(contract) is { } restored)
                    meta.Contracts.Add(restored);
            }
        }

        meta.PendingEvents.Clear();
        meta.EventLog.Clear();
        if (dto.Events is not null)
        {
            foreach (var pending in dto.Events.Pending ?? new List<PendingEventDto>())
            {
                if (pending is not null && ToPendingEvent(pending) is { } restored)
                    meta.PendingEvents.Add(restored);
            }

            foreach (var entry in dto.Events.Log ?? new List<EventLogDto>())
            {
                if (entry is not null)
                    meta.AppendEventLog(ToEventLogEntry(entry));
            }

            meta.EventCooldownGameHour = dto.Events.CooldownGameHour;
        }
        else
        {
            meta.EventCooldownGameHour = null;
        }

        meta.Rovers.Clear();
        foreach (var rover in dto.Rovers ?? new List<RoverDto>())
        {
            if (rover is not null && ToRover(rover) is { } restored)
                meta.Rovers.Add(restored);
        }
    }

    private static ContractBlockDto ToContractBlock(CampaignMeta meta)
    {
        var block = new ContractBlockDto();
        foreach (var contract in meta.Contracts.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var dto = new ContractDto
            {
                Id = contract.Id,
                DefId = contract.DefId,
                Status = contract.Status.ToString().ToLowerInvariant(),
                AcceptedGameHour = contract.AcceptedGameHour,
                DeadlineGameHour = contract.DeadlineGameHour,
                Progress = new Dictionary<string, double>(
                    contract.Progress ?? new Dictionary<string, double>(StringComparer.Ordinal),
                    StringComparer.Ordinal),
                RewardClaimed = contract.RewardClaimed,
            };

            switch (contract.Status)
            {
                case ContractStatus.Completed:
                    block.Completed.Add(dto);
                    break;
                case ContractStatus.Failed:
                    block.Failed.Add(dto);
                    break;
                default:
                    block.Active.Add(dto);
                    break;
            }
        }

        return block;
    }

    private static CampaignContract? ToContract(ContractDto stored)
    {
        if (string.IsNullOrWhiteSpace(stored.DefId))
            return null;

        var id = Guid.TryParse(stored.Id, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();

        return new CampaignContract
        {
            Id = id.ToString("D"),
            DefId = stored.DefId,
            Status = Enum.TryParse<ContractStatus>(stored.Status, ignoreCase: true, out var status)
                ? status
                : ContractStatus.Active,
            AcceptedGameHour = stored.AcceptedGameHour,
            DeadlineGameHour = stored.DeadlineGameHour,
            Progress = new Dictionary<string, double>(
                stored.Progress ?? new Dictionary<string, double>(StringComparer.Ordinal),
                StringComparer.Ordinal),
            RewardClaimed = stored.RewardClaimed,
        };
    }

    private static EventBlockDto ToEventBlock(CampaignMeta meta) => new()
    {
        Pending = meta.PendingEvents
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList(),
        CooldownGameHour = meta.EventCooldownGameHour,
        Log = meta.EventLog.Select(ToDto).ToList(),
    };

    private static PendingEventDto ToDto(PendingCampaignEvent pending) => new()
    {
        Id = pending.Id,
        DefId = pending.DefId,
        RaisedGameHour = pending.RaisedGameHour,
        TargetRegion = HexString(pending.TargetRegion),
        Choices = (pending.Choices ?? new List<string>()).ToList(),
    };

    private static PendingCampaignEvent? ToPendingEvent(PendingEventDto stored)
    {
        if (string.IsNullOrWhiteSpace(stored.DefId))
            return null;

        var id = Guid.TryParse(stored.Id, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();

        return new PendingCampaignEvent
        {
            Id = id.ToString("D"),
            DefId = stored.DefId,
            RaisedGameHour = stored.RaisedGameHour,
            TargetRegion = TryParseHex(stored.TargetRegion, out var region) ? (ulong)region : null,
            Choices = (stored.Choices ?? new List<string>())
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static EventLogDto ToDto(CampaignEventLogEntry entry) => new()
    {
        GameHour = entry.GameHour,
        DefId = entry.DefId,
        Choice = entry.Choice,
        Summary = entry.Summary,
    };

    private static CampaignEventLogEntry ToEventLogEntry(EventLogDto stored) => new()
    {
        GameHour = stored.GameHour,
        DefId = stored.DefId,
        Choice = stored.Choice,
        Summary = stored.Summary,
    };

    private static RoverDto ToDto(RoverState rover) => new()
    {
        Id = rover.Id,
        Name = rover.Name,
        DefId = rover.DefId,
        Status = rover.Status.ToString().ToLowerInvariant(),
        Health = rover.Health,
        SpeedKmh = rover.SpeedKmh,
        LocationRegion = HexString(rover.LocationRegion),
        LocationHex = HexString(rover.LocationHex),
        DestinationRegion = HexString(rover.DestinationRegion),
        DestinationHex = HexString(rover.DestinationHex),
        DepartureGameHour = rover.DepartureGameHour,
        ArrivalGameHour = rover.ArrivalGameHour,
        Cargo = new Dictionary<string, double>(
            rover.Cargo ?? new Dictionary<string, double>(StringComparer.Ordinal),
            StringComparer.Ordinal),
    };

    private static RoverState? ToRover(RoverDto stored)
    {
        if (string.IsNullOrWhiteSpace(stored.DefId))
            return null;

        var id = Guid.TryParse(stored.Id, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();

        return new RoverState
        {
            Id = id.ToString("D"),
            Name = string.IsNullOrWhiteSpace(stored.Name) ? "Rover" : stored.Name,
            DefId = stored.DefId,
            Status = Enum.TryParse<RoverStatus>(stored.Status, ignoreCase: true, out var status)
                ? status
                : RoverStatus.Idle,
            Health = double.IsFinite(stored.Health)
                ? Math.Clamp(stored.Health, 0.0, 100.0)
                : CampaignDefaults.DefaultRoverHealth,
            SpeedKmh = double.IsFinite(stored.SpeedKmh) && stored.SpeedKmh > 0.0
                ? stored.SpeedKmh
                : CampaignDefaults.DefaultRoverSpeedKmh,
            LocationRegion = TryParseHex(stored.LocationRegion, out var region) ? (ulong)region : null,
            LocationHex = TryParseHex(stored.LocationHex, out var hex) ? (ulong)hex : null,
            DestinationRegion = TryParseHex(stored.DestinationRegion, out var targetRegion) ? (ulong)targetRegion : null,
            DestinationHex = TryParseHex(stored.DestinationHex, out var targetHex) ? (ulong)targetHex : null,
            DepartureGameHour = stored.DepartureGameHour,
            ArrivalGameHour = stored.ArrivalGameHour,
            Cargo = new Dictionary<string, double>(
                stored.Cargo ?? new Dictionary<string, double>(StringComparer.Ordinal),
                StringComparer.Ordinal),
        };
    }

    private static BuildingFailure? ToFailure(FailureDto? stored)
    {
        if (stored is null || string.IsNullOrWhiteSpace(stored.Cause))
            return null;

        return new BuildingFailure
        {
            Cause = stored.Cause,
            Severity = double.IsFinite(stored.Severity) ? Math.Clamp(stored.Severity, 0.0, 1.0) : 0.0,
            StartedGameHour = stored.StartedGameHour,
            RepairStartGameHour = stored.RepairStartGameHour,
            RepairCompleteGameHour = stored.RepairCompleteGameHour,
        };
    }

    private static void WriteAtomic(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json, Utf8NoBom);

        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);

        File.Move(temp, path, overwrite: true);
    }

    private static void EnsureSupported(string format, int version, string path)
    {
        if (!string.Equals(format, FormatId, StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: expected format '{FormatId}', got '{format}'.");

        if (version < OldestSupportedVersion || version > Version)
            throw new InvalidDataException(
                $"{path}: save version {version} is not supported (runtime supports " +
                $"{OldestSupportedVersion}..{Version}).");
    }

    private static object? ToPrimitive(object? value) => value switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        string or bool or double or float or int or long or short => value,
        _ => null,
    };

    private static string HexString(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? HexString(ulong? value) => value is { } index ? HexString(index) : null;

    private static string HexString(H3Index value) => HexString((ulong)value);

    private static bool TryParseHex(string? text, out H3Index hex)
    {
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            hex = new H3Index(value);
            return true;
        }

        hex = default;
        return false;
    }

    private sealed class SlotDto
    {
        public string Format { get; set; } = SlotFormatId;
        public int Version { get; set; } = SlotVersion;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset LastPlayedUtc { get; set; }
        public int BuildingCount { get; set; }
        public int VisitedRegionCount { get; set; }
        public string BodyKey { get; set; } = string.Empty;
        public double Credits { get; set; }
        public SlotViewDto? View { get; set; }
    }

    private sealed class SlotViewDto
    {
        public string Mode { get; set; } = "globe";
        public float GlobeYawDegrees { get; set; } = -90f;
        public float GlobePitchDegrees { get; set; } = -14.3f;
        public float GlobeZoom { get; set; } = 1f;
        public int ColonyRotationSteps { get; set; }
        public string Overlay { get; set; } = "terrain";
        public int? SelectedHexOrdinal { get; set; }
    }

    private sealed class CampaignDto
    {
        public string Format { get; set; } = FormatId;
        public int Version { get; set; } = CampaignStore.Version;
        public string BodyKey { get; set; } = string.Empty;
        public int FineResolution { get; set; }
        public int RegionResolution { get; set; }
        public int RegionCount { get; set; }
        public string GeneratedUtc { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public double GameTimeHours { get; set; }
        public double Credits { get; set; }
        public double SciencePoints { get; set; }
        public ulong RngSeed { get; set; }
        public ulong RngState { get; set; }
        public List<string> UnlockedTechs { get; set; } = new();
        public Dictionary<string, double> Stockpile { get; set; } = new();
        public List<string> VisitedRegions { get; set; } = new();
        public string? ActiveRegion { get; set; }
        public ContractBlockDto? Contracts { get; set; }
        public EventBlockDto? Events { get; set; }
        public List<RoverDto>? Rovers { get; set; }
    }

    private sealed class ContractBlockDto
    {
        public List<ContractDto> Active { get; set; } = new();
        public List<ContractDto> Completed { get; set; } = new();
        public List<ContractDto> Failed { get; set; } = new();
    }

    private sealed class ContractDto
    {
        public string Id { get; set; } = string.Empty;
        public string DefId { get; set; } = string.Empty;
        public string Status { get; set; } = "active";
        public double AcceptedGameHour { get; set; }
        public double? DeadlineGameHour { get; set; }
        public Dictionary<string, double> Progress { get; set; } = new(StringComparer.Ordinal);
        public bool RewardClaimed { get; set; }
    }

    private sealed class EventBlockDto
    {
        public List<PendingEventDto> Pending { get; set; } = new();
        public double? CooldownGameHour { get; set; }
        public List<EventLogDto> Log { get; set; } = new();
    }

    private sealed class PendingEventDto
    {
        public string Id { get; set; } = string.Empty;
        public string DefId { get; set; } = string.Empty;
        public double RaisedGameHour { get; set; }
        public string? TargetRegion { get; set; }
        public List<string> Choices { get; set; } = new();
    }

    private sealed class EventLogDto
    {
        public double GameHour { get; set; }
        public string DefId { get; set; } = string.Empty;
        public string? Choice { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class RoverDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "Rover";
        public string DefId { get; set; } = string.Empty;
        public string Status { get; set; } = "idle";
        public double Health { get; set; } = 100.0;
        public double SpeedKmh { get; set; } = 8.0;
        public string? LocationRegion { get; set; }
        public string? LocationHex { get; set; }
        public string? DestinationRegion { get; set; }
        public string? DestinationHex { get; set; }
        public double? DepartureGameHour { get; set; }
        public double? ArrivalGameHour { get; set; }
        public Dictionary<string, double> Cargo { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RegionDto
    {
        public string Format { get; set; } = FormatId;
        public int Version { get; set; } = CampaignStore.Version;
        public string Region { get; set; } = string.Empty;

        /// <summary>v1/v2 shards: one flat record per centre hex.</summary>
        public List<LegacyBuildingDto>? Buildings { get; set; }

        /// <summary>v3 shards: sites keyed by parent fine hex.</summary>
        public List<SiteDto>? Sites { get; set; }
    }

    private sealed class SiteDto
    {
        public string ParentHex { get; set; } = string.Empty;
        public List<BuildingDto> Installations { get; set; } = new();
    }

    private abstract class BuildingFieldsDto
    {
        public string? Id { get; set; }
        public string DefId { get; set; } = string.Empty;
        public double Progress { get; set; }
        public double Health { get; set; } = 100.0;
        public string Status { get; set; } = nameof(BuildingStatus.Planned);
        public bool Paused { get; set; }
        public string? StatusReason { get; set; }
        public Dictionary<string, double>? StoredResources { get; set; }
        public double ConstructionStartTime { get; set; }
        public double? ConstructionCompleteTime { get; set; }
        public Dictionary<string, object>? State { get; set; }
        public FailureDto? Failure { get; set; }
    }

    private sealed class FailureDto
    {
        public string Cause { get; set; } = string.Empty;
        public double Severity { get; set; }
        public double StartedGameHour { get; set; }
        public double? RepairStartGameHour { get; set; }
        public double? RepairCompleteGameHour { get; set; }
    }

    private sealed class LegacyBuildingDto : BuildingFieldsDto
    {
        public string CenterHex { get; set; } = string.Empty;
    }

    private sealed class BuildingDto : BuildingFieldsDto
    {
        public LocalCellDto LocalCenter { get; set; } = new();
    }

    private sealed class LocalCellDto
    {
        public int Q { get; set; }
        public int R { get; set; }
    }
}

/// <summary>Header of <c>campaign.json</c>, read without applying the save.</summary>
public sealed record CampaignHeader(
    BakeFingerprint Fingerprint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    double Credits,
    int VisitedRegionCount,
    ulong? ActiveRegionIndex);

/// <summary>Region and building totals derived from a slot's shards.</summary>
public readonly record struct StoredCounts(int Regions, int Buildings, int Unreadable);
