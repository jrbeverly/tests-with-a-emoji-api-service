using System.Text.Json;
using System.Text.Json.Serialization;

var manifestDir = ParseArg(args, "--manifest-dir");
var alias = ParseArg(args, "--alias");

if (manifestDir is null || alias is null)
{
    Console.Error.WriteLine("Usage: EmojiService.Downstream --manifest-dir <path> --alias <alias>");
    Environment.Exit(2);
}

var manifestPath = Path.Combine(manifestDir, "manifest.json");
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Manifest not found: {manifestPath}");
    Environment.Exit(1);
}

var json = await File.ReadAllTextAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<EmojiManifestModel>(json);

if (manifest is null)
{
    Console.Error.WriteLine("Failed to parse manifest");
    Environment.Exit(1);
}

var byAlias = BuildAliasMap(manifest);

if (!byAlias.TryGetValue(alias, out var entry))
{
    Console.Error.WriteLine($"Alias '{alias}' not found in manifest");
    Environment.Exit(1);
}

Console.WriteLine(
    JsonSerializer.Serialize(new ResolveResult(entry.Uid, entry.PrimaryAlias, entry.DisplayName))
);
return 0;

static Dictionary<string, ManifestEmojiEntryModel> BuildAliasMap(EmojiManifestModel manifest)
{
    var map = new Dictionary<string, ManifestEmojiEntryModel>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in manifest.Emoji)
    {
        map.TryAdd(entry.PrimaryAlias, entry);
        foreach (var sec in entry.SecondaryAliases)
            map.TryAdd(sec, entry);
    }
    return map;
}

static string? ParseArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

public sealed record EmojiManifestModel
{
    [JsonPropertyName("manifest_id")]
    public string ManifestId { get; init; } = string.Empty;

    [JsonPropertyName("consumer_id")]
    public string ConsumerId { get; init; } = string.Empty;

    [JsonPropertyName("emoji")]
    public List<ManifestEmojiEntryModel> Emoji { get; init; } = [];
}

public sealed record ManifestEmojiEntryModel
{
    [JsonPropertyName("uid")]
    public string Uid { get; init; } = string.Empty;

    [JsonPropertyName("primary_alias")]
    public string PrimaryAlias { get; init; } = string.Empty;

    [JsonPropertyName("secondary_aliases")]
    public List<string> SecondaryAliases { get; init; } = [];

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string ContentType { get; init; } = string.Empty;

    [JsonPropertyName("asset_reference")]
    public string AssetReference { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    [JsonPropertyName("categories")]
    public List<string> Categories { get; init; } = [];

    [JsonPropertyName("owner")]
    public string Owner { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }
}

public sealed record ResolveResult(string Uid, string PrimaryAlias, string DisplayName);
