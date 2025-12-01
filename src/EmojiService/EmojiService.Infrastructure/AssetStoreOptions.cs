namespace EmojiService.Infrastructure;

public sealed record AssetStoreOptions
{
    public const string Section = "AssetStore";

    public string BasePath { get; init; } = "var/asset-store";
}
