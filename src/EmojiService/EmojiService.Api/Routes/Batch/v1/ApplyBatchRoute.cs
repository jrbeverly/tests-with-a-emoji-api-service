using System.Text.Json;
using System.Text.RegularExpressions;
using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;
using DomainAlias = EmojiService.Domain.Alias;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Api.Routes.Batch.v1;

public static class ApplyBatchRoute
{
    private const long MaxFileSize = 256 * 1024;
    private const long MaxSvgFileSize = 64 * 1024;
    private const int MaxItemsPerBatch = 100;

    private static readonly HashSet<string> AllowedContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "image/png",
        "image/gif",
        "image/jpeg",
        "image/svg+xml",
    };

    private const string TagOrCategoryPattern = @"^[a-z0-9][a-z0-9_-]*$";
    private static readonly Regex TagOrCategoryRegex = new(
        TagOrCategoryPattern,
        RegexOptions.Compiled
    );

    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapPost("/apply", Handler.HandleAsync)
                .WithName("ApplyBatch")
                .WithTags("Batch")
                .Produces<Response>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
        }
    }

    public sealed record ManifestItem
    {
        public string? FileKey { get; init; }
        public string? Alias { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public List<string>? Tags { get; init; }
        public List<string>? Categories { get; init; }
        public string? Owner { get; init; }
    }

    private sealed record ManifestEnvelope
    {
        public List<ManifestItem>? Items { get; init; }
    }

    public sealed record Response
    {
        public required string ManifestId { get; init; }
        public required DateTime AppliedAt { get; init; }
        public required int TotalItems { get; init; }
        public required int Succeeded { get; init; }
        public required int Failed { get; init; }
        public required int Skipped { get; init; }
        public required IReadOnlyList<ItemResult> Items { get; init; }
    }

    public sealed record ItemResult
    {
        public required int Index { get; init; }
        public required string FileKey { get; init; }
        public required string Status { get; init; }
        public string? EmojiUid { get; init; }
        public IReadOnlyList<ItemIssue>? Issues { get; init; }
    }

    public sealed record ItemIssue
    {
        public required string Field { get; init; }
        public required string Code { get; init; }
        public required string Message { get; init; }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            HttpRequest request,
            IEmojiRepository repository,
            IEmojiIndex index,
            IAssetStore assetStore,
            IAuditLog auditLog,
            IAliasPolicyService aliasPolicy,
            ISyncOrchestrator syncOrchestrator
        )
        {
            if (!request.HasFormContentType)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Invalid content type",
                        Detail = "Request must be multipart/form-data",
                    }
                );

            var form = await request.ReadFormAsync();

            var manifestJson = form["manifest"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(manifestJson))
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Missing manifest",
                        Detail = "The 'manifest' form part is required",
                    }
                );

            ManifestEnvelope manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestEnvelope>(
                    manifestJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    }
                )!;
            }
            catch (JsonException)
            {
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Invalid manifest",
                        Detail = "The manifest must be valid JSON",
                    }
                );
            }

            if (manifest?.Items is null || manifest.Items.Count == 0)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Empty manifest",
                        Detail = "The manifest must contain at least one item",
                    }
                );

            if (manifest.Items.Count > MaxItemsPerBatch)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = "Batch too large",
                        Detail = $"The manifest must contain at most {MaxItemsPerBatch} items",
                    }
                );

            var manifestId = System.Ulid.NewUlid().ToString().ToLowerInvariant();
            var now = DateTime.UtcNow;
            var fileKeysInBatch = new HashSet<string>(StringComparer.Ordinal);
            var aliasesInBatch = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<ItemResult>(manifest.Items.Count);
            var createdUids = new List<string>();

            for (var i = 0; i < manifest.Items.Count; i++)
            {
                var item = manifest.Items[i];
                var issues = new List<ItemIssue>();
                var fileKey = (item.FileKey ?? string.Empty).Trim();
                var alias = (item.Alias ?? string.Empty).Trim();
                var displayName = item.DisplayName ?? alias;

                // ── Validation (same rules as validate) ──

                if (string.IsNullOrWhiteSpace(fileKey))
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "file_key",
                            Code = "missing_field",
                            Message = "file_key is required",
                        }
                    );
                }
                else if (!fileKeysInBatch.Add(fileKey))
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "file_key",
                            Code = "duplicate_file_key",
                            Message = $"file_key '{fileKey}' is duplicated within this batch",
                        }
                    );
                }

                if (string.IsNullOrWhiteSpace(alias))
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "alias",
                            Code = "missing_field",
                            Message = "alias is required",
                        }
                    );
                }
                else
                {
                    if (!aliasesInBatch.Add(alias))
                    {
                        issues.Add(
                            new ItemIssue
                            {
                                Field = "alias",
                                Code = "duplicate_alias",
                                Message = $"Alias '{alias}' is duplicated within this batch",
                            }
                        );
                    }

                    var aliasValidation = DomainAlias.Create(alias);
                    if (aliasValidation.IsFailure)
                    {
                        issues.Add(
                            new ItemIssue
                            {
                                Field = "alias",
                                Code = "alias_invalid",
                                Message = aliasValidation.Error!.Message,
                            }
                        );
                    }
                    else
                    {
                        var policyResult = aliasPolicy.CheckName(aliasValidation.Value!.Value);
                        if (policyResult.Status == AliasPolicyStatus.Blocked)
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "alias",
                                    Code = "alias_blocked",
                                    Message = $"Alias '{alias}' is blocked: {policyResult.Reason}",
                                }
                            );
                        }
                        else if (policyResult.Status == AliasPolicyStatus.Reserved)
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "alias",
                                    Code = "alias_reserved",
                                    Message = $"Alias '{alias}' is reserved: {policyResult.Reason}",
                                }
                            );
                        }

                        var existingEmoji = await repository.GetByAliasAsync(
                            aliasValidation.Value!.Value
                        );
                        if (existingEmoji is not null)
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "alias",
                                    Code = "alias_taken",
                                    Message =
                                        $"Alias '{alias}' is already in use by emoji '{existingEmoji.Uid.Value}'",
                                }
                            );
                        }
                    }
                }

                if (
                    !string.IsNullOrWhiteSpace(fileKey)
                    && !issues.Any(iss => iss.Code == "duplicate_file_key")
                )
                {
                    var file = form.Files.GetFile(fileKey);
                    if (file is null || file.Length == 0)
                    {
                        issues.Add(
                            new ItemIssue
                            {
                                Field = "file",
                                Code = "missing_file",
                                Message = $"No file found for file_key '{fileKey}'",
                            }
                        );
                    }
                    else
                    {
                        if (!AllowedContentTypes.Contains(file.ContentType))
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "file",
                                    Code = "unsupported_format",
                                    Message =
                                        $"Content type '{file.ContentType}' is not supported. Allowed types: {string.Join(", ", AllowedContentTypes)}",
                                }
                            );
                        }
                        else
                        {
                            var sizeLimit = file.ContentType.Equals(
                                "image/svg+xml",
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? MaxSvgFileSize
                                : MaxFileSize;

                            if (file.Length > sizeLimit)
                            {
                                issues.Add(
                                    new ItemIssue
                                    {
                                        Field = "file",
                                        Code = "file_too_large",
                                        Message =
                                            $"File size {file.Length} exceeds the maximum of {sizeLimit} bytes",
                                    }
                                );
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "display_name",
                            Code = "missing_field",
                            Message = "Display name must not be empty",
                        }
                    );
                }
                else if (displayName.Length > 200)
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "display_name",
                            Code = "field_too_long",
                            Message = "Display name must be at most 200 characters",
                        }
                    );
                }

                var rawTags = PrepareList(item.Tags);
                if (rawTags.Count > DomainEmoji.MaxTags)
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "tags",
                            Code = "too_many_tags",
                            Message = $"Too many tags (max {DomainEmoji.MaxTags} allowed)",
                        }
                    );
                }
                else
                {
                    foreach (var tag in rawTags)
                    {
                        if (tag.Length > DomainEmoji.MaxTagLength)
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "tags",
                                    Code = "field_too_long",
                                    Message =
                                        $"Tag must be at most {DomainEmoji.MaxTagLength} characters",
                                }
                            );
                            break;
                        }
                        if (!TagOrCategoryRegex.IsMatch(tag))
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "tags",
                                    Code = "tag_invalid",
                                    Message =
                                        $"Tag '{tag}' must start with a letter or digit and contain only lowercase letters, digits, hyphens, and underscores",
                                }
                            );
                            break;
                        }
                    }
                }

                var rawCategories = PrepareList(item.Categories);
                if (rawCategories.Count > DomainEmoji.MaxCategories)
                {
                    issues.Add(
                        new ItemIssue
                        {
                            Field = "categories",
                            Code = "too_many_categories",
                            Message =
                                $"Too many categories (max {DomainEmoji.MaxCategories} allowed)",
                        }
                    );
                }
                else
                {
                    foreach (var category in rawCategories)
                    {
                        if (category.Length > DomainEmoji.MaxCategoryLength)
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "categories",
                                    Code = "field_too_long",
                                    Message =
                                        $"Category must be at most {DomainEmoji.MaxCategoryLength} characters",
                                }
                            );
                            break;
                        }
                        if (!TagOrCategoryRegex.IsMatch(category))
                        {
                            issues.Add(
                                new ItemIssue
                                {
                                    Field = "categories",
                                    Code = "category_invalid",
                                    Message =
                                        $"Category '{category}' must start with a letter or digit and contain only lowercase letters, digits, hyphens, and underscores",
                                }
                            );
                            break;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.Owner))
                {
                    if (item.Owner.Trim().Length > DomainEmoji.MaxOwnerLength)
                    {
                        issues.Add(
                            new ItemIssue
                            {
                                Field = "owner",
                                Code = "field_too_long",
                                Message =
                                    $"Owner must be at most {DomainEmoji.MaxOwnerLength} characters",
                            }
                        );
                    }
                }

                // ── Skip if validation failed ──
                if (issues.Count > 0)
                {
                    results.Add(
                        new ItemResult
                        {
                            Index = i,
                            FileKey = string.IsNullOrWhiteSpace(fileKey) ? "" : fileKey,
                            Status = "skipped",
                            Issues = issues,
                        }
                    );
                    continue;
                }

                // ── Persist valid item ──
                try
                {
                    var uid = EmojiUid.Create(System.Ulid.NewUlid().ToString().ToLowerInvariant());
                    if (uid.IsFailure)
                    {
                        results.Add(
                            new ItemResult
                            {
                                Index = i,
                                FileKey = fileKey,
                                Status = "failed",
                                Issues =
                                [
                                    new ItemIssue
                                    {
                                        Field = "internal",
                                        Code = "uid_generation_failed",
                                        Message = "Failed to generate emoji UID",
                                    },
                                ],
                            }
                        );
                        continue;
                    }

                    var emojiUid = uid.Value!;
                    var file = form.Files.GetFile(fileKey)!;

                    await using var fileStream = file.OpenReadStream();
                    await assetStore.StoreAsync(emojiUid.Value, fileStream, file.ContentType);

                    var emojiResult = DomainEmoji.Create(
                        emojiUid,
                        DomainAlias.Create(alias).Value!,
                        displayName.Trim(),
                        (item.Description ?? string.Empty).Trim(),
                        file.ContentType,
                        emojiUid.Value,
                        now,
                        now,
                        tags: rawTags,
                        categories: rawCategories,
                        owner: item.Owner?.Trim()
                    );

                    if (emojiResult.IsFailure)
                    {
                        results.Add(
                            new ItemResult
                            {
                                Index = i,
                                FileKey = fileKey,
                                Status = "failed",
                                Issues =
                                [
                                    new ItemIssue
                                    {
                                        Field = "internal",
                                        Code = "domain_validation_failed",
                                        Message = emojiResult.Error!.Message,
                                    },
                                ],
                            }
                        );
                        continue;
                    }

                    var saveResult = await repository.SaveAsync(emojiResult.Value!);
                    if (saveResult.IsFailure)
                    {
                        results.Add(
                            new ItemResult
                            {
                                Index = i,
                                FileKey = fileKey,
                                Status = "failed",
                                Issues =
                                [
                                    new ItemIssue
                                    {
                                        Field = "alias",
                                        Code = saveResult.Error!.Code,
                                        Message = saveResult.Error.Message,
                                    },
                                ],
                            }
                        );
                        continue;
                    }

                    var emoji = emojiResult.Value!;
                    index.Upsert(emoji);
                    createdUids.Add(emoji.Uid.Value);

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

                    results.Add(
                        new ItemResult
                        {
                            Index = i,
                            FileKey = fileKey,
                            Status = "created",
                            EmojiUid = emoji.Uid.Value,
                        }
                    );
                }
                catch (Exception ex)
                {
                    results.Add(
                        new ItemResult
                        {
                            Index = i,
                            FileKey = fileKey,
                            Status = "failed",
                            Issues =
                            [
                                new ItemIssue
                                {
                                    Field = "internal",
                                    Code = "persistence_error",
                                    Message = ex.Message,
                                },
                            ],
                        }
                    );
                }
            }

            // ── Batch audit event ──
            var batchAuditEvent = new AuditEvent
            {
                EventId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
                Actor = "system",
                Action = "emoji.batch_apply",
                SubjectUid = $"MANIFEST#{manifestId}",
                After = JsonSerializer.Serialize(
                    new
                    {
                        manifest_id = manifestId,
                        total_items = results.Count,
                        succeeded = results.Count(r => r.Status == "created"),
                        failed = results.Count(r => r.Status == "failed"),
                        skipped = results.Count(r => r.Status == "skipped"),
                        created_uids = createdUids,
                    }
                ),
                OccurredAt = now,
            };
            await auditLog.WriteAsync(batchAuditEvent);

            _ = syncOrchestrator.NotifyEmojiChangedAsync(CancellationToken.None);

            var succeeded = results.Count(r => r.Status == "created");
            var failed = results.Count(r => r.Status == "failed");
            var skipped = results.Count(r => r.Status == "skipped");

            return TypedResults.Ok(
                new Response
                {
                    ManifestId = manifestId,
                    AppliedAt = now,
                    TotalItems = results.Count,
                    Succeeded = succeeded,
                    Failed = failed,
                    Skipped = skipped,
                    Items = results,
                }
            );
        }

        private static IReadOnlyList<string> PrepareList(List<string>? items)
        {
            if (items is null)
                return Array.Empty<string>();

            return items
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => i.Trim().ToLowerInvariant())
                .ToList();
        }
    }
}
