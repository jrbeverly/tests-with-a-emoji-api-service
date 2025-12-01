using System.Text.Json;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Emoji.v1;

public static class UploadEmojiRoute
{
    private const long MaxFileSize = 5 * 1024 * 1024;
    private static readonly HashSet<string> AllowedContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "image/png",
        "image/gif",
        "image/jpeg",
        "image/svg+xml",
    };

    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/upload", Handler.HandleAsync)
                .WithName("UploadEmoji")
                .WithTags("Emoji")
                .Produces<Response>(StatusCodes.Status201Created)
                .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
        }
    }

    public record Response
    {
        public required string Uid { get; init; }
        public required string PrimaryAlias { get; init; }
        public required string AssetUrl { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            HttpRequest request,
            IEmojiRepository repository,
            IEmojiIndex index,
            IAssetStore assetStore,
            IAuditLog auditLog,
            ISyncOrchestrator syncOrchestrator
        )
        {
            if (!request.HasFormContentType)
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["contentType"] = ["Request must be multipart/form-data"],
                    }
                );

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var primaryAlias = form["primaryAlias"].FirstOrDefault() ?? string.Empty;
            var displayName = form["displayName"].FirstOrDefault() ?? string.Empty;
            var description = form["description"].FirstOrDefault();
            var tags = form["tags"].FirstOrDefault();
            var categories = form["categories"].FirstOrDefault();
            var owner = form["owner"].FirstOrDefault();

            var validationErrors = new Dictionary<string, string[]>();

            if (file is null || file.Length == 0)
                validationErrors["file"] = ["File is required"];
            else if (!AllowedContentTypes.Contains(file.ContentType))
                validationErrors["file"] =
                [
                    $"Content type '{file.ContentType}' is not supported. Allowed types: {string.Join(", ", AllowedContentTypes)}",
                ];
            else if (file.Length > MaxFileSize)
                validationErrors["file"] =
                [
                    $"File size exceeds maximum of {MaxFileSize / 1024 / 1024} MB",
                ];

            var aliasResult = DomainAlias.Create(primaryAlias);
            if (aliasResult.IsFailure)
                validationErrors["primaryAlias"] = [aliasResult.Error!.Message];

            if (string.IsNullOrWhiteSpace(displayName))
                validationErrors["displayName"] = ["Display name must not be empty"];

            if (validationErrors.Count > 0)
                return TypedResults.ValidationProblem(validationErrors);

            var uid = EmojiUid.Create(System.Ulid.NewUlid().ToString().ToLowerInvariant());
            if (uid.IsFailure)
                return TypedResults.Problem(
                    detail: "Failed to generate emoji UID",
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Internal Server Error"
                );

            var emojiUid = uid.Value!;

            await using var fileStream = file!.OpenReadStream();
            await assetStore.StoreAsync(emojiUid.Value, fileStream, file.ContentType);

            var now = DateTime.UtcNow;
            var tagsList = ParseCommaSeparated(tags);
            var categoriesList = ParseCommaSeparated(categories);

            var emojiResult = DomainEmoji.Create(
                emojiUid,
                aliasResult.Value!,
                displayName.Trim(),
                (description ?? string.Empty).Trim(),
                file.ContentType,
                emojiUid.Value,
                now,
                now,
                tags: tagsList,
                categories: categoriesList,
                owner: owner?.Trim()
            );

            if (emojiResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = emojiResult.Error!.Code,
                        Detail = emojiResult.Error.Message,
                    }
                );

            var saveResult = await repository.SaveAsync(emojiResult.Value!);
            if (saveResult.IsFailure)
            {
                var saveStatusCode = saveResult.Error!.Code switch
                {
                    "E501" => StatusCodes.Status409Conflict,
                    "E600" => StatusCodes.Status409Conflict,
                    "E601" => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status500InternalServerError,
                };
                return TypedResults.Problem(
                    detail: saveResult.Error.Message,
                    statusCode: saveStatusCode,
                    title: saveResult.Error.Code
                );
            }

            var emoji = emojiResult.Value!;
            index.Upsert(emoji);

            var auditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "emoji.upload",
                SubjectUid = emoji.Uid.Value,
                After = JsonSerializer.Serialize(emoji),
                OccurredAt = now,
            };
            await auditLog.WriteAsync(auditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            return TypedResults.Created(
                $"/api/v1/emoji/{emoji.Uid.Value}",
                new Response
                {
                    Uid = emoji.Uid.Value,
                    PrimaryAlias = emoji.PrimaryAlias.Value,
                    AssetUrl = $"/api/v1/emoji/{emoji.Uid.Value}/asset",
                    CreatedAt = emoji.CreatedAt,
                }
            );
        }

        private static IReadOnlyList<string> ParseCommaSeparated(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => v.Trim().ToLowerInvariant())
                .Where(v => v.Length > 0)
                .ToList();
        }
    }
}
