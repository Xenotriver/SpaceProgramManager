using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using H3;
using Sim.Buildings;

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

    /// <summary>Current save schema version.</summary>
    public const int Version = 1;

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

    /// <summary>True when a campaign already exists at this location.</summary>
    public bool Exists => File.Exists(MetaPath);

    /// <summary>Path of one region's shard.</summary>
    public string RegionPath(ulong regionIndex)
        => Path.Combine(RootDirectory, "regions", $"{regionIndex.ToString(CultureInfo.InvariantCulture)}.json");

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
            Stockpile = new Dictionary<string, double>(meta.Stockpile, StringComparer.Ordinal),
            VisitedRegions = meta.VisitedRegions
                .OrderBy(index => index)
                .Select(HexString)
                .ToList(),
            ActiveRegion = meta.ActiveRegionIndex is { } active ? HexString(active) : null,
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
            ActiveRegionIndex = TryParseHex(dto.ActiveRegion, out var active) ? active : null,
        };

        foreach (var (resource, amount) in dto.Stockpile)
            meta.Stockpile[resource] = amount;

        foreach (var text in dto.VisitedRegions)
        {
            if (TryParseHex(text, out var index))
                meta.VisitedRegions.Add(index);
        }

        return meta;
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
        var buildings = new List<BuildingInstance>(dto.Buildings.Count);

        foreach (var stored in dto.Buildings)
        {
            if (string.IsNullOrWhiteSpace(stored.DefId))
            {
                warnings.Add("building without a definition id");
                continue;
            }

            if (!TryParseHex(stored.CenterHex, out var centerHex))
            {
                warnings.Add($"{stored.DefId}: invalid center hex '{stored.CenterHex}'");
                continue;
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

            buildings.Add(new BuildingInstance
            {
                Id = Guid.TryParse(stored.Id, out var id) && id != Guid.Empty ? id : Guid.NewGuid(),
                DefId = stored.DefId,
                CenterHex = centerHex,
                Footprint = Array.Empty<H3Index>(),
                Progress = stored.Progress,
                Health = stored.Health,
                Status = Enum.TryParse<BuildingStatus>(stored.Status, ignoreCase: true, out var status)
                    ? status
                    : BuildingStatus.Planned,
                StoredResources = new Dictionary<string, double>(
                    stored.StoredResources ?? new Dictionary<string, double>(),
                    StringComparer.Ordinal),
                ConstructionStartTime = stored.ConstructionStartTime,
                ConstructionCompleteTime = stored.ConstructionCompleteTime,
                State = state,
            });
        }

        return new RegionLoadResult(regionIndex, buildings, warnings);
    }

    /// <summary>Writes one region shard atomically.</summary>
    public void SaveRegion(ulong regionIndex, IReadOnlyList<BuildingInstance> buildings)
    {
        ArgumentNullException.ThrowIfNull(buildings);

        var dto = new RegionDto
        {
            Format = FormatId,
            Version = Version,
            Region = HexString(regionIndex),
            Buildings = buildings.Select(ToDto).ToList(),
        };

        WriteAtomic(RegionPath(regionIndex), JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>Deletes a region shard (and its backup), if present.</summary>
    public void DeleteRegion(ulong regionIndex)
    {
        var path = RegionPath(regionIndex);
        File.Delete(path);
        File.Delete(path + ".bak");
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
            CenterHex = HexString(building.CenterHex),
            Progress = building.Progress,
            Health = building.Health,
            Status = building.Status.ToString(),
            StoredResources = new Dictionary<string, double>(building.StoredResources, StringComparer.Ordinal),
            ConstructionStartTime = building.ConstructionStartTime,
            ConstructionCompleteTime = building.ConstructionCompleteTime,
            State = state.Count > 0 ? state : null,
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

        if (version != Version)
            throw new InvalidDataException(
                $"{path}: save version {version} is not supported (runtime supports {Version}).");
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
        public Dictionary<string, double> Stockpile { get; set; } = new();
        public List<string> VisitedRegions { get; set; } = new();
        public string? ActiveRegion { get; set; }
    }

    private sealed class RegionDto
    {
        public string Format { get; set; } = FormatId;
        public int Version { get; set; } = CampaignStore.Version;
        public string Region { get; set; } = string.Empty;
        public List<BuildingDto> Buildings { get; set; } = new();
    }

    private sealed class BuildingDto
    {
        public string? Id { get; set; }
        public string DefId { get; set; } = string.Empty;
        public string CenterHex { get; set; } = string.Empty;
        public double Progress { get; set; }
        public double Health { get; set; } = 100.0;
        public string Status { get; set; } = nameof(BuildingStatus.Planned);
        public Dictionary<string, double>? StoredResources { get; set; }
        public double ConstructionStartTime { get; set; }
        public double? ConstructionCompleteTime { get; set; }
        public Dictionary<string, object>? State { get; set; }
    }
}
