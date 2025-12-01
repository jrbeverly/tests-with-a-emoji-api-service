using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class TriggerSyncRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/{id}/sync", Handler.HandleAsync)
                .WithName("TriggerSync")
                .WithTags("Consumers")
                .Produces<Response>(StatusCodes.Status202Accepted)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
        }
    }

    public record Response
    {
        public required string ResultId { get; init; }
        public required string ConsumerId { get; init; }
        public required string ManifestId { get; init; }
        public required string Status { get; init; }
        public required int TotalEmoji { get; init; }
        public required int SyncedEmoji { get; init; }
        public required int SkippedEmoji { get; init; }
        public required int FailedEmoji { get; init; }
        public string? ErrorMessage { get; init; }
        public required DateTime StartedAt { get; init; }
        public required DateTime CompletedAt { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string id,
            IConsumerRepository consumerRepository,
            ISyncOrchestrator syncOrchestrator,
            CancellationToken ct
        )
        {
            var consumer = await consumerRepository.GetByIdAsync(id);
            if (consumer is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Consumer with ID '{id}' not found",
                    }
                );

            if (consumer.State != Consumer.StateActive)
                return TypedResults.Conflict(
                    new ProblemDetails
                    {
                        Status = 409,
                        Title = "Conflict",
                        Detail = $"Consumer '{id}' is not active (state: {consumer.State})",
                    }
                );

            var record = await syncOrchestrator.SyncConsumerAsync(consumer, ct);

            var responseStatusCode =
                record.Status == "success"
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status500InternalServerError;

            return TypedResults.Json(
                new Response
                {
                    ResultId = record.ResultId,
                    ConsumerId = record.ConsumerId,
                    ManifestId = record.ManifestId,
                    Status = record.Status,
                    TotalEmoji = record.TotalEmoji,
                    SyncedEmoji = record.SyncedEmoji,
                    SkippedEmoji = record.SkippedEmoji,
                    FailedEmoji = record.FailedEmoji,
                    ErrorMessage = record.ErrorMessage,
                    StartedAt = record.StartedAt,
                    CompletedAt = record.CompletedAt,
                },
                statusCode: responseStatusCode
            );
        }
    }
}
