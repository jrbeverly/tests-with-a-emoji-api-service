namespace EmojiService.Domain;

public interface IManifestGenerator
{
    EmojiManifest Generate(Consumer consumer);
}

public sealed record EmojiManifest
{
    public required string ManifestId { get; init; }
    public required string ConsumerId { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required int ManifestVersion { get; init; }
    public required IReadOnlyList<ManifestEmojiEntry> Emoji { get; init; }
    public ConsumerSubsetFilter? Filter { get; init; }
}

public sealed record ManifestEmojiEntry
{
    public required string Uid { get; init; }
    public required string PrimaryAlias { get; init; }
    public required IReadOnlyList<string> SecondaryAliases { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string ContentType { get; init; }
    public required string AssetReference { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }
    public required string Owner { get; init; }
    public required string State { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
