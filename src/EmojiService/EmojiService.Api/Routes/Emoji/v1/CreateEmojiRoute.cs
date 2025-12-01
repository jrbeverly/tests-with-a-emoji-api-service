using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class CreateEmojiRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/", Handler.HandleAsync)
                .WithName("CreateEmoji")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status201Created)
                .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
        }
    }

    public record Request
    {
        [Required]
        public string PrimaryAlias { get; init; } = string.Empty;

        [Required]
        public string DisplayName { get; init; } = string.Empty;

        [Required]
        public string ContentType { get; init; } = string.Empty;

        [Required]
        public string AssetReference { get; init; } = string.Empty;
        public string? Description { get; init; }
        public List<string>? Tags { get; init; }
        public List<string>? Categories { get; init; }
        public string? Owner { get; init; }
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
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromBody] Request request,
            IEmojiRepository repository,
            IEmojiIndex index,
            IAuditLog auditLog,
            ISyncOrchestrator syncOrchestrator
        )
        {
            var aliasResult = DomainAlias.Create(request.PrimaryAlias);
            if (aliasResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = aliasResult.Error!.Code,
                        Detail = aliasResult.Error.Message,
                    }
                );

            var uid = EmojiUid.Create(System.Ulid.NewUlid().ToString().ToLowerInvariant());
            if (uid.IsFailure)
                return TypedResults.Problem(
                    detail: "Failed to generate emoji UID",
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Internal Server Error"
                );

            var now = DateTime.UtcNow;

            var emojiResult = DomainEmoji.Create(
                uid.Value!,
                aliasResult.Value!,
                request.DisplayName,
                request.Description ?? string.Empty,
                request.ContentType,
                request.AssetReference,
                now,
                now,
                request.Tags,
                request.Categories,
                request.Owner
            );

            if (emojiResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = emojiResult.Error!.Code,
                        Detail = emojiResult.Error.Message,
                    }
                );

            var saveResult = await repository.SaveAsync(emojiResult.Value!);
            if (saveResult.IsFailure)
            {
                var saveStatusCode = saveResult.Error!.Code switch
                {
                    "E501" => StatusCodes.Status400BadRequest,
                    "E600" => StatusCodes.Status409Conflict,
                    "E601" => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status500InternalServerError,
                };
                return TypedResults.Problem(
                    detail: saveResult.Error.Message,
                    statusCode: saveStatusCode,
                    title: saveResult.Error.Code
                );
            }

            var emoji = emojiResult.Value!;
            index.Upsert(emoji);

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "emoji.upload",
                SubjectUid = emoji.Uid.Value,
                After = JsonSerializer.Serialize(emoji),
                OccurredAt = now,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Created($"/api/v1/emoji/{emoji.Uid.Value}", MapToResponse(emoji));
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
            CreatedAt = emoji.CreatedAt,
            UpdatedAt = emoji.UpdatedAt,
        };
    }
}
