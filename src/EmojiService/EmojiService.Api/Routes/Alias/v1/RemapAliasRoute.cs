using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Alias.v1;

public static class RemapAliasRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPut("/{alias}/target", Handler.HandleAsync)
                .WithName("RemapAlias")
                .WithTags("Alias")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        }
    }

    public record Request
    {
        [Required]
        public string Uid { get; init; } = string.Empty;
        public bool OverrideProtection { get; init; }
        public string? Reason { get; init; }
    }

    public record Response
    {
        public required string Alias { get; init; }
        public required string PreviousEmojiUid { get; init; }
        public required string TargetEmojiUid { get; init; }
        public required bool OverrideUsed { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string alias,
            [FromBody] Request request,
            IEmojiRepository repository,
            IEmojiIndex index,
            IAuditLog auditLog,
            ISyncOrchestrator syncOrchestrator
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

            var targetUidResult = EmojiUid.Create(request.Uid);
            if (targetUidResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = targetUidResult.Error!.Code,
                        Detail = targetUidResult.Error.Message,
                    }
                );

            var normalizedAlias = aliasResult.Value!.Value;

            var oldEmoji = await repository.GetByAliasAsync(normalizedAlias);
            if (oldEmoji is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "E544",
                        Detail = $"Alias '{normalizedAlias}' not found",
                    }
                );

            var remapResult = request.OverrideProtection
                ? await repository.RemapAliasWithOverrideAsync(
                    normalizedAlias,
                    targetUidResult.Value!
                )
                : await repository.RemapAliasAsync(normalizedAlias, targetUidResult.Value!);
            if (remapResult.IsFailure)
            {
                var statusCode = remapResult.Error!.Code switch
                {
                    "E541" => StatusCodes.Status404NotFound,
                    "E542" => StatusCodes.Status404NotFound,
                    "E543" => StatusCodes.Status403Forbidden,
                    "E544" => StatusCodes.Status404NotFound,
                    "E545" => StatusCodes.Status409Conflict,
                    "E601" => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest,
                };
                return TypedResults.Problem(
                    detail: remapResult.Error.Message,
                    statusCode: statusCode,
                    title: remapResult.Error.Code
                );
            }

            var refreshedOldEmoji = await repository.GetByUidAsync(oldEmoji.Uid);
            var refreshedNewEmoji = await repository.GetByUidAsync(targetUidResult.Value!);

            if (refreshedOldEmoji is not null)
                index.Upsert(refreshedOldEmoji);
            if (refreshedNewEmoji is not null)
                index.Upsert(refreshedNewEmoji);

            var now = DateTime.UtcNow;

            var auditAction = request.OverrideProtection ? "alias.remap_override" : "alias.remap";
            var auditReason = request.OverrideProtection
                ? $"Protected alias '{normalizedAlias}' remapped from '{oldEmoji.Uid.Value}' to '{targetUidResult.Value!.Value}'"
                : $"Alias '{normalizedAlias}' remapped from '{oldEmoji.Uid.Value}' to '{targetUidResult.Value!.Value}'";
            if (request.Reason is not null)
                auditReason += $" (reason: {request.Reason})";

            await auditLog.WriteAsync(
                new AuditEvent
                {
                    EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                    Actor = "system",
                    Action = auditAction,
                    SubjectUid = oldEmoji.Uid.Value,
                    Before = JsonSerializer.Serialize(
                        new { alias = normalizedAlias, emoji_uid = oldEmoji.Uid.Value }
                    ),
                    After = JsonSerializer.Serialize(
                        new
                        {
                            alias = normalizedAlias,
                            emoji_uid = targetUidResult.Value!.Value,
                            override_protection = request.OverrideProtection,
                        }
                    ),
                    OccurredAt = now,
                    Reason = auditReason,
                }
            );

            await auditLog.WriteAsync(
                new AuditEvent
                {
                    EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                    Actor = "system",
                    Action = auditAction,
                    SubjectUid = targetUidResult.Value!.Value,
                    Before = JsonSerializer.Serialize(new { }),
                    After = JsonSerializer.Serialize(
                        new
                        {
                            alias = normalizedAlias,
                            source_emoji_uid = oldEmoji.Uid.Value,
                            override_protection = request.OverrideProtection,
                        }
                    ),
                    OccurredAt = now,
                    Reason = $"Alias '{normalizedAlias}' gained from '{oldEmoji.Uid.Value}'",
                }
            );

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Ok(
                new Response
                {
                    Alias = normalizedAlias,
                    PreviousEmojiUid = oldEmoji.Uid.Value,
                    TargetEmojiUid = targetUidResult.Value!.Value,
                    OverrideUsed = request.OverrideProtection,
                }
            );
        }
    }
}
