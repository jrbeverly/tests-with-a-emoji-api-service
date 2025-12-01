using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class DeleteConsumerRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapDelete("/{id}", Handler.HandleAsync)
                .WithName("DeleteConsumer")
                .WithTags("Consumers")
                .Produces(StatusCodes.Status204NoContent)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string id,
            IConsumerRepository repository
        )
        {
            var existing = await repository.GetByIdAsync(id);
            if (existing is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Consumer with ID '{id}' not found",
                    }
                );

            var deleteResult = await repository.DeleteAsync(id);
            if (deleteResult.IsFailure)
                return TypedResults.Problem(
                    detail: deleteResult.Error!.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: deleteResult.Error.Code
                );

            return TypedResults.NoContent();
        }
    }
}
