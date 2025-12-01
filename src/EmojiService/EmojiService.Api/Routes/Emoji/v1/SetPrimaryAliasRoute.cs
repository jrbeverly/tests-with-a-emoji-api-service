using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class SetPrimaryAliasRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPut("/{uid}/aliases/{alias}/primary", Handler.HandleAsync)
                .WithName("SetPrimaryAlias")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
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
            [FromRoute] string alias,
            IEmojiRepository repository,
            IEmojiIndex index,
            IAuditLog auditLog,
            ISyncOrchestrator syncOrchestrator
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

            var promoteResult = await repository.SetPrimaryAliasAsync(uidResult.Value!, alias);
            if (promoteResult.IsFailure)
            {
                var statusCode = promoteResult.Error!.Code switch
                {
                    "E530" => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status400BadRequest,
                };
                return TypedResults.Problem(
                    detail: promoteResult.Error.Message,
                    statusCode: statusCode,
                    title: promoteResult.Error.Code
                );
            }

            var emoji = await repository.GetByUidAsync(uidResult.Value!);
            index.Upsert(emoji!);

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "alias.set_primary",
                SubjectUid = uid,
                After = JsonSerializer.Serialize(new { alias }),
                OccurredAt = DateTime.UtcNow,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Ok(MapToResponse(emoji!));
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
            Tags = emoji.Tags,
            Categories = emoji.Categories,
            Owner = emoji.Owner,
            LifecycleState = emoji.LifecycleState.Value,
            CreatedAt = emoji.CreatedAt,
            UpdatedAt = emoji.UpdatedAt,
        };
    }
}
