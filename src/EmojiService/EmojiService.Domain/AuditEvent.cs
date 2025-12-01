namespace EmojiService.Domain;

public sealed record AuditEvent
{
    public required string EventId { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string SubjectUid { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public required DateTime OccurredAt { get; init; }
    public string? Reason { get; init; }
}
