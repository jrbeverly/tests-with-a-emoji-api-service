using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class RetireAliasRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapDelete("/{uid}/aliases/{alias}", Handler.HandleAsync)
                .WithName("RetireAlias")
                .WithTags("Emoji")
                .Produces(StatusCodes.Status204NoContent)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        }
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

            var retireResult = await repository.RetireAliasAsync(uidResult.Value!, alias);
            if (retireResult.IsFailure)
            {
                var statusCode = retireResult.Error!.Code switch
                {
                    "E520" => StatusCodes.Status404NotFound,
                    "E521" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest,
                };
                return TypedResults.Problem(
                    detail: retireResult.Error.Message,
                    statusCode: statusCode,
                    title: retireResult.Error.Code
                );
            }

            var emoji = await repository.GetByUidAsync(uidResult.Value!);
            index.Upsert(emoji!);

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "alias.retire",
                SubjectUid = uid,
                After = JsonSerializer.Serialize(new { alias }),
                OccurredAt = DateTime.UtcNow,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.NoContent();
        }
    }
}
