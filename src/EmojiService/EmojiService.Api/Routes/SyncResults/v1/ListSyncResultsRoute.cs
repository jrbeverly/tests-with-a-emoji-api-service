using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.SyncResults.v1;

public static class ListSyncResultsRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/{consumerId}/sync-results", Handler.HandleAsync)
                .WithName("ListSyncResults")
                .WithTags("SyncResults")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        }
    }

    public record Response
    {
        public required IReadOnlyList<SyncResultItem> Items { get; init; }
        public string? Cursor { get; init; }
        public required bool HasMore { get; init; }
    }

    public record SyncResultItem
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
            [FromRoute] string consumerId,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            IConsumerRepository consumerRepository,
            ISyncResultRepository syncResultRepository
        )
        {
            var consumer = await consumerRepository.GetByIdAsync(consumerId);
            if (consumer is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Consumer with ID '{consumerId}' not found",
                    }
                );

            var pageSize = limit.GetValueOrDefault(20);
            if (pageSize < 1)
                pageSize = 1;
            if (pageSize > 100)
                pageSize = 100;

            var page = await syncResultRepository.ListByConsumerAsync(consumerId, cursor, pageSize);

            return TypedResults.Ok(
                new Response
                {
                    Items = page.Items.Select(MapToItem).ToList(),
                    Cursor = page.Cursor,
                    HasMore = page.HasMore,
                }
            );
        }
    }

    private static SyncResultItem MapToItem(SyncResultRecord record) =>
        new()
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
        };
}
