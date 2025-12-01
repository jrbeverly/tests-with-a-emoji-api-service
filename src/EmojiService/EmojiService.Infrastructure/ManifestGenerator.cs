using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class ManifestGenerator : IManifestGenerator
{
    public const int CurrentManifestVersion = 1;

    private readonly IEmojiIndex _index;

    public ManifestGenerator(IEmojiIndex index)
    {
        _index = index;
    }

    public EmojiManifest Generate(Consumer consumer)
    {
        var manifestId = System.Ulid.NewUlid().ToString().ToLowerInvariant();
        var now = DateTime.UtcNow;

        var entries = CollectMatchingEmoji(consumer.SubsetFilter);

        return new EmojiManifest
        {
            ManifestId = manifestId,
            ConsumerId = consumer.Id,
            GeneratedAt = now,
            ManifestVersion = CurrentManifestVersion,
            Emoji = entries,
            Filter = consumer.SubsetFilter,
        };
    }

    private IReadOnlyList<ManifestEmojiEntry> CollectMatchingEmoji(ConsumerSubsetFilter? filter)
    {
        var states = GetEffectiveStates(filter);
        var allItems = new Dictionary<string, EmojiSearchItem>();

        foreach (var state in states)
        {
            string? cursor = null;
            do
            {
                var request = new SearchRequest
                {
                    Tags = filter?.Tags ?? Array.Empty<string>(),
                    Categories = filter?.Categories ?? Array.Empty<string>(),
                    Owner = filter?.Owner,
                    AliasPrefix = filter?.AliasPrefix,
                    State = state,
                    Cursor = cursor,
                    Limit = 100,
                };

                var result = _index.Search(request);

                foreach (var item in result.Items)
                    allItems.TryAdd(item.Uid, item);

                cursor = result.HasMore ? result.Cursor : null;
            } while (cursor is not null);
        }

        return allItems.Values.OrderBy(e => e.Uid).Select(MapToEntry).ToList();
    }

    private static IReadOnlyList<string> GetEffectiveStates(ConsumerSubsetFilter? filter)
    {
        if (filter?.State is not null)
            return [filter.State];

        return LifecycleState.Resolvable.Select(s => s.Value).ToList();
    }

    private static ManifestEmojiEntry MapToEntry(EmojiSearchItem item) =>
        new()
        {
            Uid = item.Uid,
            PrimaryAlias = item.PrimaryAlias,
            SecondaryAliases = item.SecondaryAliases,
            DisplayName = item.DisplayName,
            Description = item.Description,
            ContentType = item.ContentType,
            AssetReference = item.AssetReference,
            Tags = item.Tags,
            Categories = item.Categories,
            Owner = item.Owner,
            State = item.State,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };
}
