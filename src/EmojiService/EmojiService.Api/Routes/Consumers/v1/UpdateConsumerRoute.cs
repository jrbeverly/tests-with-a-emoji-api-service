using System.ComponentModel.DataAnnotations;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class UpdateConsumerRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPut("/{id}", Handler.HandleAsync)
                .WithName("UpdateConsumer")
                .WithTags("Consumers")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        }
    }

    public record Request
    {
        [Required]
        public string DisplayName { get; init; } = string.Empty;

        [Required]
        public string Adapter { get; init; } = "local-fs";

        [Required]
        public string TargetPath { get; init; } = string.Empty;
        public ConsumerSubsetFilter? SubsetFilter { get; init; }
        public string TriggerMode { get; init; } = "manual";

        [Required]
        public string State { get; init; } = "active";
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
            [FromBody] Request request,
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

            var now = DateTime.UtcNow;

            var consumerResult = Consumer.UpdateFrom(
                existing,
                request.DisplayName,
                request.Adapter,
                request.TargetPath,
                request.TriggerMode,
                request.State,
                now,
                request.SubsetFilter
            );

            if (consumerResult.IsFailure)
            {
                var statusCode = consumerResult.Error!.Code switch
                {
                    "E722" or "E723" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest,
                };
                return TypedResults.Problem(
                    detail: consumerResult.Error.Message,
                    statusCode: statusCode,
                    title: consumerResult.Error.Code
                );
            }

            var saveResult = await repository.SaveAsync(consumerResult.Value!);
            if (saveResult.IsFailure)
                return TypedResults.Problem(
                    detail: saveResult.Error!.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: saveResult.Error.Code
                );

            return TypedResults.Ok(MapToResponse(consumerResult.Value!));
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
