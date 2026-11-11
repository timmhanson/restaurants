using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public interface IGroupSettingsStore
{
    Task<GroupSettings> GetAsync(int groupId, CancellationToken ct = default);
    Task SaveAsync(int groupId, GroupSettings settings, CancellationToken ct = default);
    string Description { get; }
}

public class StorageOptions
{
    /// <summary>Full connection string (Azurite or account key). Takes precedence when set.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Account blob endpoint, e.g. https://account.blob.core.windows.net - used with managed identity / DefaultAzureCredential.</summary>
    public string? AccountUrl { get; set; }
    public string ContainerName { get; set; } = "group-settings";
    /// <summary>Local folder used when neither Azure option is configured (development fallback).</summary>
    public string? LocalFolder { get; set; }
}

/// <summary>Stores each group's settings as a JSON blob, one blob per investment group.</summary>
public class BlobGroupSettingsStore : IGroupSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobGroupSettingsStore> _log;
    private bool _ensured;

    public BlobGroupSettingsStore(StorageOptions options, ILogger<BlobGroupSettingsStore> log)
    {
        _log = log;
        BlobServiceClient service = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobServiceClient(options.ConnectionString)
            : new BlobServiceClient(new Uri(options.AccountUrl!), new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(options.ContainerName);
        Description = $"Azure Blob Storage ({_container.Uri.Host}/{options.ContainerName})";
    }

    public string Description { get; }

    private static string BlobName(int groupId) => $"group-{groupId}.json";

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_ensured) return;
        try { await _container.CreateIfNotExistsAsync(cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 403) { _log.LogWarning("No permission to create container; assuming it exists."); }
        _ensured = true;
    }

    public async Task<GroupSettings> GetAsync(int groupId, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(BlobName(groupId));
        try
        {
            var result = await blob.DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<GroupSettings>(JsonOpts) ?? new GroupSettings();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new GroupSettings();
        }
    }

    public async Task SaveAsync(int groupId, GroupSettings settings, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(BlobName(groupId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOpts);
        await blob.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Metadata = new Dictionary<string, string> { ["groupId"] = groupId.ToString(), ["schemaVersion"] = settings.SchemaVersion.ToString() }
        }, ct);
    }
}

/// <summary>Development fallback: one JSON file per group under a local folder. Same shape as the blob store.</summary>
public class FileGroupSettingsStore(StorageOptions options, IWebHostEnvironment env) : IGroupSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _folder = options.LocalFolder ?? Path.Combine(env.ContentRootPath, "App_Data", "group-settings");

    public string Description => $"Local files ({_folder})";

    private string PathFor(int groupId) => Path.Combine(_folder, $"group-{groupId}.json");

    public async Task<GroupSettings> GetAsync(int groupId, CancellationToken ct = default)
    {
        var path = PathFor(groupId);
        if (!File.Exists(path)) return new GroupSettings();
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<GroupSettings>(fs, JsonOpts, ct) ?? new GroupSettings();
    }

    public async Task SaveAsync(int groupId, GroupSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_folder);
        await using var fs = File.Create(PathFor(groupId));
        await JsonSerializer.SerializeAsync(fs, settings, JsonOpts, ct);
    }
}
