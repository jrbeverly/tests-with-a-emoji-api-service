using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class GetEmojiRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/{uid}", Handler.HandleAsync)
                .WithName("GetEmoji")
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
            [FromRoute] string uid,
            IEmojiRepository repository
        )
        {
            var uidResult = EmojiUid.Create(uid);
            if (uidResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = uidResult.Error!.Code,
                        Detail = uidResult.Error.Message,
                    }
                );

            var emoji = await repository.GetByUidAsync(uidResult.Value!);
            if (emoji is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Emoji with UID '{uid}' not found",
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
