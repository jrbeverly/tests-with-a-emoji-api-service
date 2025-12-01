namespace EmojiService.Domain;

public interface IEmojiIndex
{
    SearchResult Search(SearchRequest request);
    void Upsert(Emoji emoji);
    void Remove(string uid);
}

public record SearchRequest
{
    public string? AliasPrefix { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
    public string? Owner { get; init; }
    public string State { get; init; } = LifecycleState.Active.Value;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 20;
}

public record SearchResult
{
    public required IReadOnlyList<EmojiSearchItem> Items { get; init; }
    public string? Cursor { get; init; }
    public required bool HasMore { get; init; }
}

public record EmojiSearchItem
{
    public required string Uid { get; init; }
    public required string PrimaryAlias { get; init; }
    public required IReadOnlyList<string> SecondaryAliases { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string ContentType { get; init; }
    public required string AssetReference { get; init; }
    public required string State { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }
    public required string Owner { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
