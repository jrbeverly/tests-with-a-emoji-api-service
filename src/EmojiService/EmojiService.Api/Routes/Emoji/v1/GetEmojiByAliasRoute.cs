using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class GetEmojiByAliasRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/by-alias/{alias}", Handler.HandleAsync)
                .WithName("GetEmojiByAlias")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        }
    }

    public record Response
    {
        public required string Uid { get; init; }
        public required string PrimaryAlias { get; init; }
        public required IReadOnlyList<string> Aliases { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required string ContentType { get; init; }
        public required string AssetReference { get; init; }
        public required string AssetUrl { get; init; }
        public required IReadOnlyList<string> Tags { get; init; }
        public required IReadOnlyList<string> Categories { get; init; }
        public required string Owner { get; init; }
        public required string LifecycleState { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string alias,
            IEmojiRepository repository
        )
        {
            var aliasResult = DomainAlias.Create(alias);
            if (aliasResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = aliasResult.Error!.Code,
                        Detail = aliasResult.Error.Message,
                    }
                );

            var emoji = await repository.GetByAliasAsync(aliasResult.Value!.Value);
            if (emoji is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Emoji with alias '{aliasResult.Value.Value}' not found",
                    }
                );

            return TypedResults.Ok(MapToResponse(emoji));
        }
    }

    private static Response MapToResponse(DomainEmoji emoji)
    {
        var allAliases = new List<string> { emoji.PrimaryAlias.Value };
        allAliases.AddRange(emoji.SecondaryAliases);

        return new Response
        {
            Uid = emoji.Uid.Value,
            PrimaryAlias = emoji.PrimaryAlias.Value,
            Aliases = allAliases,
            DisplayName = emoji.DisplayName,
            Description = emoji.Description,
            ContentType = emoji.ContentType,
            AssetReference = emoji.AssetReference,
            AssetUrl = $"/assets/{emoji.Uid.Value}",
            Tags = emoji.Tags,
            Categories = emoji.Categories,
            Owner = emoji.Owner,
            LifecycleState = emoji.LifecycleState.Value,
            CreatedAt = emoji.CreatedAt,
            UpdatedAt = emoji.UpdatedAt,
        };
    }
}
