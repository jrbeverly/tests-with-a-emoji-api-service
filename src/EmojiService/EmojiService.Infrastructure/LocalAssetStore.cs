using EmojiService.Domain;

namespace EmojiService.Infrastructure;

// Per ADR-0008: local filesystem asset store with UID-only addressing.
// Layout: {basePath}/{uid}/original.{ext}

public class LocalAssetStore : IAssetStore
{
    private static readonly Dictionary<string, string> ContentTypeToExtension = new()
    {
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/jpeg"] = ".jpg",
        ["image/svg+xml"] = ".svg",
    };

    private readonly string _basePath;

    public LocalAssetStore(AssetStoreOptions options)
    {
        _basePath = Path.GetFullPath(options.BasePath);
    }

    public async Task StoreAsync(
        string uid,
        Stream content,
        string contentType,
        CancellationToken ct = default
    )
    {
        var dir = EnsureDirectory(uid);
        var ext = ContentTypeToExtension.TryGetValue(contentType, out var mapped) ? mapped : ".bin";
        var filePath = Path.Combine(dir, $"original{ext}");

        await using var fileStream = File.Create(filePath);
        await content.CopyToAsync(fileStream, ct);
    }

    public Task<AssetStream> GetAsync(string uid, CancellationToken ct = default)
    {
        var filePath = FindFile(uid);
        if (filePath is null)
            throw new FileNotFoundException($"Asset not found for UID '{uid}'");

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = GetContentType(ext);
        var fileInfo = new FileInfo(filePath);

        var stream = File.OpenRead(filePath);
        return Task.FromResult(new AssetStream(stream, contentType, fileInfo.Length));
    }

    public Task DeleteAsync(string uid, CancellationToken ct = default)
    {
        var dir = GetDirectory(uid);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string uid, CancellationToken ct = default)
    {
        var filePath = FindFile(uid);
        return Task.FromResult(filePath is not null);
    }

    private string EnsureDirectory(string uid)
    {
        var dir = GetDirectory(uid);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GetDirectory(string uid) => Path.Combine(_basePath, uid);

    private string? FindFile(string uid)
    {
        var dir = GetDirectory(uid);
        if (!Directory.Exists(dir))
            return null;

        foreach (var candidate in Directory.EnumerateFiles(dir, "original.*"))
        {
            return candidate;
        }

        return null;
    }

    private static string GetContentType(string extension) =>
        extension switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
}
