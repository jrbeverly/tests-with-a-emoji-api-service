using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.SyncResults.v1;

public static class GetSyncResultRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/sync-results/{resultId}", Handler.HandleAsync)
                .WithName("GetSyncResult")
                .WithTags("SyncResults")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
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
            [FromRoute] string resultId,
            ISyncResultRepository syncResultRepository
        )
        {
            var record = await syncResultRepository.GetByIdAsync(resultId);
            if (record is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Sync result with ID '{resultId}' not found",
                    }
                );

            return TypedResults.Ok(
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
                }
            );
        }
    }
}
