namespace EmojiService.Domain;

public sealed record ConsumerSubsetFilter
{
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public string? Owner { get; init; }
    public string? AliasPrefix { get; init; }
    public string? State { get; init; }
}
