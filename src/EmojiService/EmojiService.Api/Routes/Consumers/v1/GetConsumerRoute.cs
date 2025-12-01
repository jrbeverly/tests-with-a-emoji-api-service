using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class GetConsumerRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/{id}", Handler.HandleAsync)
                .WithName("GetConsumer")
                .WithTags("Consumers")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
        }
    }

    public record Response
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string Adapter { get; init; }
        public required string TargetPath { get; init; }
        public ConsumerSubsetFilter? SubsetFilter { get; init; }
        public required string TriggerMode { get; init; }
        public required string State { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string id,
            IConsumerRepository repository
        )
        {
            var consumer = await repository.GetByIdAsync(id);
            if (consumer is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Consumer with ID '{id}' not found",
                    }
                );

            return TypedResults.Ok(MapToResponse(consumer));
        }
    }

    private static Response MapToResponse(Consumer consumer) =>
        new()
        {
            Id = consumer.Id,
            DisplayName = consumer.DisplayName,
            Adapter = consumer.Adapter,
            TargetPath = consumer.TargetPath,
            SubsetFilter = consumer.SubsetFilter,
            TriggerMode = consumer.TriggerMode,
            State = consumer.State,
            CreatedAt = consumer.CreatedAt,
            UpdatedAt = consumer.UpdatedAt,
        };
}
