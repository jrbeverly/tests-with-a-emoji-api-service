using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class SearchEmojiRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/", Handler.HandleAsync)
                .WithName("SearchEmoji")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        }
    }

    public record EmojiItem
    {
        public required string Uid { get; init; }
        public required string PrimaryAlias { get; init; }
        public required IReadOnlyList<string> SecondaryAliases { get; init; }
        public required string DisplayName { get; init; }
        public required string State { get; init; }
        public required IReadOnlyList<string> Tags { get; init; }
        public required IReadOnlyList<string> Categories { get; init; }
        public required string Owner { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    public record Response
    {
        public required IReadOnlyList<EmojiItem> Items { get; init; }
        public string? Cursor { get; init; }
        public required bool HasMore { get; init; }
    }

    public static class Handler
    {
        public static IResult HandleAsync(
            [FromQuery] string? tag,
            [FromQuery] string? category,
            [FromQuery] string? owner,
            [FromQuery(Name = "alias_prefix")] string? aliasPrefix,
            [FromQuery] string? state,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            IEmojiIndex index
        )
        {
            if (limit.HasValue && (limit.Value < 1 || limit.Value > 100))
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Invalid limit",
                        Detail = "Limit must be between 1 and 100",
                    }
                );

            var request = new SearchRequest
            {
                AliasPrefix = aliasPrefix,
                Tags = ParseMultiValue(tag),
                Categories = ParseMultiValue(category),
                Owner = owner,
                State = state ?? DomainEmoji.StateActive,
                Cursor = cursor,
                Limit = limit ?? 20,
            };

            var stateValidation = LifecycleState.Create(request.State);
            if (stateValidation.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Invalid state",
                        Detail =
                            $"State must be one of: {string.Join(", ", LifecycleState.All.Select(s => s.Value))}",
                    }
                );

            var result = index.Search(request);

            return TypedResults.Ok(
                new Response
                {
                    Items = result.Items.Select(MapToItem).ToList(),
                    Cursor = result.Cursor,
                    HasMore = result.HasMore,
                }
            );
        }

        private static IReadOnlyList<string> ParseMultiValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => v.Trim().ToLowerInvariant())
                .Where(v => v.Length > 0)
                .ToList();
        }

        private static EmojiItem MapToItem(EmojiSearchItem item) =>
            new()
            {
                Uid = item.Uid,
                PrimaryAlias = item.PrimaryAlias,
                SecondaryAliases = item.SecondaryAliases,
                DisplayName = item.DisplayName,
                State = item.State,
                Tags = item.Tags,
                Categories = item.Categories,
                Owner = item.Owner,
                CreatedAt = item.CreatedAt,
            };
    }
}
