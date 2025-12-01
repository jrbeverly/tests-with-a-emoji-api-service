using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Consumers.v1;

public static class ListConsumersRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/", Handler.HandleAsync)
                .WithName("ListConsumers")
                .WithTags("Consumers")
                .Produces<Response>(StatusCodes.Status200OK);
        }
    }

    public record ConsumerItem
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

    public record Response
    {
        public required IReadOnlyList<ConsumerItem> Items { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromQuery(Name = "include_archived")] bool? includeArchived,
            IConsumerRepository repository
        )
        {
            var all = await repository.ScanAllAsync();

            var filtered = includeArchived.GetValueOrDefault(false)
                ? all
                : all.Where(c => c.State != Consumer.StateArchived).ToList();

            return TypedResults.Ok(new Response { Items = filtered.Select(MapToItem).ToList() });
        }
    }

    private static ConsumerItem MapToItem(Consumer consumer) =>
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
