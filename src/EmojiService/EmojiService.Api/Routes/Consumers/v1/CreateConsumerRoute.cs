using System.ComponentModel.DataAnnotations;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class CreateConsumerRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/", Handler.HandleAsync)
                .WithName("CreateConsumer")
                .WithTags("Consumers")
                .Produces<Response>(StatusCodes.Status201Created)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
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
            [FromBody] Request request,
            IConsumerRepository repository
        )
        {
            var id = System.Ulid.NewUlid().ToString().ToLowerInvariant();
            var now = DateTime.UtcNow;

            var consumerResult = Consumer.Create(
                id,
                request.DisplayName,
                request.Adapter,
                request.TargetPath,
                now,
                now,
                request.SubsetFilter,
                request.TriggerMode,
                Consumer.StateActive
            );

            if (consumerResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = consumerResult.Error!.Code,
                        Detail = consumerResult.Error.Message,
                    }
                );

            var saveResult = await repository.SaveAsync(consumerResult.Value!);
            if (saveResult.IsFailure)
                return TypedResults.Problem(
                    detail: saveResult.Error!.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: saveResult.Error.Code
                );

            return TypedResults.Created($"/consumers/{id}", MapToResponse(consumerResult.Value!));
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
