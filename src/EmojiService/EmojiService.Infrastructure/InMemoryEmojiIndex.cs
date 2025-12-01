using System.Collections.Concurrent;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class InMemoryEmojiIndex : IEmojiIndex
{
    private readonly ConcurrentDictionary<string, Emoji> _all = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byTag =
        new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byCategory =
        new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byOwner =
        new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byState =
        new();

    public SearchResult Search(SearchRequest request)
    {
        var limit = Math.Clamp(request.Limit, 1, 100);
        var state = request.State;

        var candidateUids = GetUidsByState(state);

        if (!string.IsNullOrWhiteSpace(request.AliasPrefix))
            candidateUids = FilterByAliasPrefix(candidateUids, request.AliasPrefix);

        if (request.Tags.Count > 0)
            candidateUids = IntersectByIndex(candidateUids, request.Tags, _byTag);

        if (request.Categories.Count > 0)
            candidateUids = IntersectByIndex(candidateUids, request.Categories, _byCategory);

        if (!string.IsNullOrWhiteSpace(request.Owner))
            candidateUids = IntersectByIndex(candidateUids, [request.Owner], _byOwner);

        var sorted = candidateUids
            .Select(uid => _all.TryGetValue(uid, out var emoji) ? emoji : null)
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderByDescending(e => e.Uid.Value)
            .ToList();

        int skip = 0;
        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            var cursorIndex = sorted.FindIndex(e =>
                string.Compare(e.Uid.Value, request.Cursor, StringComparison.Ordinal) < 0
            );
            if (cursorIndex >= 0)
                skip = cursorIndex;
        }

        var page = sorted.Skip(skip).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;

        if (hasMore)
            page = page.Take(limit).ToList();

        var items = page.Select(MapToItem).ToList();
        var nextCursor = items.Count > 0 ? items[^1].Uid : null;

        return new SearchResult
        {
            Items = items,
            Cursor = nextCursor,
            HasMore = hasMore,
        };
    }

    public void Upsert(Emoji emoji)
    {
        var uid = emoji.Uid.Value;

        if (_all.TryGetValue(uid, out var existing))
        {
            RemoveFromIndex(uid, existing.Tags, _byTag);
            RemoveFromIndex(uid, existing.Categories, _byCategory);
            RemoveFromIndex(uid, [existing.Owner], _byOwner);
            RemoveFromIndex(uid, [existing.LifecycleState.Value], _byState);
        }

        _all[uid] = emoji;

        IndexField(uid, emoji.Tags, _byTag);
        IndexField(uid, emoji.Categories, _byCategory);
        IndexField(uid, [emoji.Owner], _byOwner);
        IndexField(uid, [emoji.LifecycleState.Value], _byState);
    }

    public void Remove(string uid)
    {
        if (!_all.TryRemove(uid, out var emoji))
            return;

        RemoveFromIndex(uid, emoji.Tags, _byTag);
        RemoveFromIndex(uid, emoji.Categories, _byCategory);
        RemoveFromIndex(uid, [emoji.Owner], _byOwner);
        RemoveFromIndex(uid, [emoji.LifecycleState.Value], _byState);
    }

    private HashSet<string> GetUidsByState(string state)
    {
        if (_byState.TryGetValue(state, out var uids))
            return new HashSet<string>(uids.Keys);

        return new HashSet<string>();
    }

    private HashSet<string> FilterByAliasPrefix(HashSet<string> candidateUids, string prefix)
    {
        var normalized = prefix.Trim().ToLowerInvariant();
        return candidateUids
            .Where(uid =>
                _all.TryGetValue(uid, out var emoji)
                && (
                    emoji.PrimaryAlias.Value.StartsWith(normalized, StringComparison.Ordinal)
                    || emoji.SecondaryAliases.Any(a =>
                        a.StartsWith(normalized, StringComparison.Ordinal)
                    )
                )
            )
            .ToHashSet();
    }

    private static HashSet<string> IntersectByIndex(
        HashSet<string> candidateUids,
        IEnumerable<string> filters,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> index
    )
    {
        foreach (var filter in filters)
        {
            var normalized = filter.Trim().ToLowerInvariant();
            if (!index.TryGetValue(normalized, out var matchingUids))
                return new HashSet<string>();

            candidateUids.IntersectWith(matchingUids.Keys);
            if (candidateUids.Count == 0)
                break;
        }

        return candidateUids;
    }

    private static void IndexField(
        string uid,
        IEnumerable<string> values,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> index
    )
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var entry = index.GetOrAdd(value, _ => new ConcurrentDictionary<string, byte>());
            entry.TryAdd(uid, 0);
        }
    }

    private static void RemoveFromIndex(
        string uid,
        IEnumerable<string> values,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> index
    )
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (index.TryGetValue(value, out var entry))
            {
                entry.TryRemove(uid, out _);
                if (entry.IsEmpty)
                    index.TryRemove(value, out _);
            }
        }
    }

    private static EmojiSearchItem MapToItem(Emoji emoji) =>
        new()
        {
            Uid = emoji.Uid.Value,
            PrimaryAlias = emoji.PrimaryAlias.Value,
            SecondaryAliases = emoji.SecondaryAliases,
            DisplayName = emoji.DisplayName,
            Description = emoji.Description,
            ContentType = emoji.ContentType,
            AssetReference = emoji.AssetReference,
            State = emoji.LifecycleState.Value,
            Tags = emoji.Tags,
            Categories = emoji.Categories,
            Owner = emoji.Owner,
            CreatedAt = emoji.CreatedAt,
            UpdatedAt = emoji.UpdatedAt,
        };
}
