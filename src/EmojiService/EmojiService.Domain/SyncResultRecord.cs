namespace EmojiService.Domain;

public sealed record SyncResultRecord
{
    public required string ResultId { get; init; }
    public required string ConsumerId { get; init; }
    public required string ManifestId { get; init; }
    public required string Status { get; init; }
    public required int TotalEmoji { get; init; }
    public required int SyncedEmoji { get; init; }
    public required int SkippedEmoji { get; init; }
    public required int FailedEmoji { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime CompletedAt { get; init; }
}

public sealed record SyncResultPage
{
    public required IReadOnlyList<SyncResultRecord> Items { get; init; }
    public string? Cursor { get; init; }
    public required bool HasMore { get; init; }
}
