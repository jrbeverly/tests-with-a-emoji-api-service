using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class UpdateEmojiStateRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPut("/{uid}/state", Handler.HandleAsync)
                .WithName("UpdateEmojiState")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        }
    }

    public record Request
    {
        [Required]
        public string State { get; init; } = string.Empty;

        public string? Reason { get; init; }
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
        private static readonly Dictionary<(string From, string To), string> TransitionActions =
            new()
            {
                [("pending", "active")] = "emoji.approve",
                [("pending", "removed")] = "emoji.reject",
                [("active", "deprecated")] = "emoji.deprecate",
                [("active", "disabled")] = "emoji.disable",
                [("deprecated", "active")] = "emoji.promote",
                [("deprecated", "disabled")] = "emoji.disable",
                [("disabled", "active")] = "emoji.enable",
                [("disabled", "removed")] = "emoji.remove",
            };

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

            var existingEmoji = await repository.GetByUidAsync(uidResult.Value!);
            if (existingEmoji is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Emoji with UID '{uid}' not found",
                    }
                );

            var targetStateResult = LifecycleState.Create(request.State);
            if (targetStateResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = targetStateResult.Error!.Code,
                        Detail = targetStateResult.Error.Message,
                    }
                );

            var targetState = targetStateResult.Value!;
            var transitionResult = LifecycleState.ValidateTransition(
                existingEmoji.LifecycleState,
                targetState
            );
            if (transitionResult.IsFailure)
            {
                var allowed = existingEmoji
                    .LifecycleState.GetAllowedTransitions()
                    .Select(s => s.Value)
                    .OrderBy(n => n);
                return TypedResults.Conflict(
                    new ProblemDetails
                    {
                        Status = 409,
                        Title = "Invalid state transition",
                        Detail =
                            $"Cannot transition from '{existingEmoji.LifecycleState.Value}' to '{targetState.Value}'. Allowed transitions: {string.Join(", ", allowed)}",
                    }
                );
            }

            var beforeSnapshot = JsonSerializer.Serialize(existingEmoji);

            var now = DateTime.UtcNow;
            var updatedEmoji = existingEmoji with { LifecycleState = targetState, UpdatedAt = now };

            var saveResult = await repository.SaveAsync(updatedEmoji);
            if (saveResult.IsFailure)
                return TypedResults.Problem(
                    detail: saveResult.Error!.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: saveResult.Error.Code
                );

            index.Upsert(updatedEmoji);

            var actionName = TransitionActions.TryGetValue(
                (existingEmoji.LifecycleState.Value, targetState.Value),
                out var action
            )
                ? action
                : "emoji.state_changed";

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = actionName,
                SubjectUid = updatedEmoji.Uid.Value,
                Before = beforeSnapshot,
                After = JsonSerializer.Serialize(updatedEmoji),
                OccurredAt = now,
                Reason = request.Reason,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Ok(MapToResponse(updatedEmoji));
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
