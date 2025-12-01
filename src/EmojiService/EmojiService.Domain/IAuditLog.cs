namespace EmojiService.Domain;

public interface IAuditLog
{
    Task WriteAsync(AuditEvent @event);

    Task<AuditPage> GetForEmojiAsync(string uid, int limit, string? cursor);

    Task<AuditPage> GetRecentAsync(int limit, string? cursor);
}

public sealed record AuditPage
{
    public required IReadOnlyList<AuditEvent> Items { get; init; }
    public string? Cursor { get; init; }
    public required bool HasMore { get; init; }
}
