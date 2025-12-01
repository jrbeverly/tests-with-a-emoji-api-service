using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Audit.v1;

public static class GetAuditRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/", Handler.HandleAsync)
                .WithName("GetAudit")
                .WithTags("Audit")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        }
    }

    public record AuditEventItem
    {
        public required string EventId { get; init; }
        public required string Actor { get; init; }
        public required string Action { get; init; }
        public required string SubjectUid { get; init; }
        public string? Before { get; init; }
        public string? After { get; init; }
        public required DateTime OccurredAt { get; init; }
        public string? Reason { get; init; }
    }

    public record Response
    {
        public required IReadOnlyList<AuditEventItem> Items { get; init; }
        public string? Cursor { get; init; }
        public required bool HasMore { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            IAuditLog auditLog
        )
        {
            if (limit.HasValue && (limit.Value < 1 || limit.Value > 100))
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Invalid limit",
                        Detail = "Limit must be between 1 and 100",
                    }
                );

            var page = await auditLog.GetRecentAsync(limit ?? 20, cursor);

            return TypedResults.Ok(
                new Response
                {
                    Items = page.Items.Select(MapToItem).ToList(),
                    Cursor = page.Cursor,
                    HasMore = page.HasMore,
                }
            );
        }

        private static AuditEventItem MapToItem(AuditEvent e) =>
            new()
            {
                EventId = e.EventId,
                Actor = e.Actor,
                Action = e.Action,
                SubjectUid = e.SubjectUid,
                Before = e.Before,
                After = e.After,
                OccurredAt = e.OccurredAt,
                Reason = e.Reason,
            };
    }
}
