namespace EmojiService.Domain;

public interface IPropagationAdapter
{
    PropagationCapabilities Capabilities { get; }

    Task<SyncResult> SyncAsync(Consumer consumer, EmojiManifest manifest, CancellationToken ct);
}

public sealed record PropagationCapabilities
{
    public bool SupportsCreate { get; init; } = true;
    public bool SupportsUpdate { get; init; } = true;
    public bool SupportsDelete { get; init; }
    public bool SupportsManifest { get; init; } = true;
    public bool SupportsAssetFiles { get; init; }
    public int? MaxEmojiCount { get; init; }
    public long? MaxAssetSizeBytes { get; init; }
    public IReadOnlySet<string>? SupportedContentTypes { get; init; }
}

public sealed record SyncResult
{
    public required string ManifestId { get; init; }
    public required string ConsumerId { get; init; }
    public required SyncStatus Status { get; init; }
    public required int TotalEmoji { get; init; }
    public required int SyncedEmoji { get; init; }
    public required int SkippedEmoji { get; init; }
    public required int FailedEmoji { get; init; }
    public required IReadOnlyList<SyncItemResult> Items { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime CompletedAt { get; init; }
}

public sealed record SyncItemResult
{
    public required string EmojiUid { get; init; }
    public required SyncItemStatus Status { get; init; }
    public string? Reason { get; init; }
}

public enum SyncStatus
{
    Success,
    Partial,
    Failed,
}

public enum SyncItemStatus
{
    Synced,
    Skipped,
    Failed,
}
