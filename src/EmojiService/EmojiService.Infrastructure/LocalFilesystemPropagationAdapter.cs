using System.Text.Json;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class LocalFilesystemPropagationAdapter : IPropagationAdapter
{
    private static readonly Dictionary<string, string> ContentTypeToExtension = new()
    {
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/jpeg"] = ".jpg",
        ["image/svg+xml"] = ".svg",
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IAssetStore _assetStore;

    public LocalFilesystemPropagationAdapter(IAssetStore assetStore)
    {
        _assetStore = assetStore;
    }

    public PropagationCapabilities Capabilities { get; } =
        new()
        {
            SupportsCreate = true,
            SupportsUpdate = true,
            SupportsManifest = true,
            SupportsAssetFiles = true,
        };

    public async Task<SyncResult> SyncAsync(
        Consumer consumer,
        EmojiManifest manifest,
        CancellationToken ct
    )
    {
        var startedAt = DateTime.UtcNow;
        var items = new List<SyncItemResult>();

        try
        {
            var targetDir = Path.GetFullPath(consumer.TargetPath);
            Directory.CreateDirectory(targetDir);

            WriteManifestAtomically(targetDir, manifest);

            var assetsDir = Path.Combine(targetDir, "assets");
            Directory.CreateDirectory(assetsDir);

            foreach (var entry in manifest.Emoji)
            {
                ct.ThrowIfCancellationRequested();
                items.Add(await CopyAssetAsync(entry, assetsDir, ct));
            }
        }
        catch (Exception ex)
        {
            var remaining = manifest
                .Emoji.Where(e => !items.Any(i => i.EmojiUid == e.Uid))
                .Select(e => new SyncItemResult
                {
                    EmojiUid = e.Uid,
                    Status = SyncItemStatus.Failed,
                    Reason = $"Sync aborted: {ex.Message}",
                });

            items.AddRange(remaining);

            return BuildResult(
                manifest.ManifestId,
                consumer.Id,
                SyncStatus.Failed,
                items,
                ex.Message,
                startedAt,
                DateTime.UtcNow
            );
        }

        var synced = items.Count(i => i.Status == SyncItemStatus.Synced);
        var failed = items.Count(i => i.Status == SyncItemStatus.Failed);
        var skipped = items.Count(i => i.Status == SyncItemStatus.Skipped);

        var status =
            failed == 0 ? SyncStatus.Success
            : synced == 0 ? SyncStatus.Failed
            : SyncStatus.Partial;

        return BuildResult(
            manifest.ManifestId,
            consumer.Id,
            status,
            items,
            null,
            startedAt,
            DateTime.UtcNow
        );
    }

    private static void WriteManifestAtomically(string targetDir, EmojiManifest manifest)
    {
        var manifestPath = Path.Combine(targetDir, "manifest.json");
        var tempPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private async Task<SyncItemResult> CopyAssetAsync(
        ManifestEmojiEntry entry,
        string assetsDir,
        CancellationToken ct
    )
    {
        try
        {
            var asset = await _assetStore.GetAsync(entry.Uid, ct);
            await using (asset.Content)
            {
                var ext = ContentTypeToExtension.TryGetValue(entry.ContentType, out var mapped)
                    ? mapped
                    : ".bin";
                var assetPath = Path.Combine(assetsDir, $"{entry.Uid}{ext}");

                await using var fileStream = File.Create(assetPath);
                await asset.Content.CopyToAsync(fileStream, ct);
            }

            return new SyncItemResult { EmojiUid = entry.Uid, Status = SyncItemStatus.Synced };
        }
        catch (FileNotFoundException)
        {
            return new SyncItemResult
            {
                EmojiUid = entry.Uid,
                Status = SyncItemStatus.Skipped,
                Reason = "Asset not found in store",
            };
        }
        catch (Exception ex)
        {
            return new SyncItemResult
            {
                EmojiUid = entry.Uid,
                Status = SyncItemStatus.Failed,
                Reason = ex.Message,
            };
        }
    }

    private static SyncResult BuildResult(
        string manifestId,
        string consumerId,
        SyncStatus status,
        IReadOnlyList<SyncItemResult> items,
        string? errorMessage,
        DateTime startedAt,
        DateTime completedAt
    )
    {
        return new SyncResult
        {
            ManifestId = manifestId,
            ConsumerId = consumerId,
            Status = status,
            TotalEmoji = items.Count,
            SyncedEmoji = items.Count(i => i.Status == SyncItemStatus.Synced),
            SkippedEmoji = items.Count(i => i.Status == SyncItemStatus.Skipped),
            FailedEmoji = items.Count(i => i.Status == SyncItemStatus.Failed),
            Items = items,
            ErrorMessage = errorMessage,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
    }
}
