using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class AddAliasRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/{uid}/aliases", Handler.HandleAsync)
                .WithName("AddAlias")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status201Created)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        }
    }

    public record Request
    {
        [Required]
        public string Alias { get; init; } = string.Empty;
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
            [FromBody] Request request,
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

            var aliasResult = DomainAlias.Create(request.Alias);
            if (aliasResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = aliasResult.Error!.Code,
                        Detail = aliasResult.Error.Message,
                    }
                );

            var addResult = await repository.AddAliasAsync(
                uidResult.Value!,
                aliasResult.Value!.Value
            );
            if (addResult.IsFailure)
            {
                var statusCode = addResult.Error!.Code switch
                {
                    "E513" => StatusCodes.Status409Conflict,
                    "E510" => StatusCodes.Status404NotFound,
                    "E600" => StatusCodes.Status409Conflict,
                    "E601" => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest,
                };
                return TypedResults.Problem(
                    detail: addResult.Error.Message,
                    statusCode: statusCode,
                    title: addResult.Error.Code
                );
            }

            var emoji = await repository.GetByUidAsync(uidResult.Value!);
            index.Upsert(emoji!);

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "alias.add",
                SubjectUid = uid,
                After = JsonSerializer.Serialize(new { alias = request.Alias }),
                OccurredAt = DateTime.UtcNow,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Created($"/api/v1/emoji/{emoji!.Uid.Value}", MapToResponse(emoji));
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
