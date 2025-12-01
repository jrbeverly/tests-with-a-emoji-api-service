using System.Text.RegularExpressions;

namespace EmojiService.Domain;

public sealed record Emoji
{
    public const int MaxTags = 10;
    public const int MaxCategories = 5;
    public const int MaxTagLength = 50;
    public const int MaxCategoryLength = 50;
    public const int MaxOwnerLength = 100;
    private const string TagOrCategoryCharsPattern = @"^[a-z0-9][a-z0-9_-]*$";

    public const string StateActive = "active";
    public const string StateDeprecated = "deprecated";

    public EmojiUid Uid { get; init; } = null!;
    public Alias PrimaryAlias { get; init; } = null!;
    public IReadOnlyList<string> SecondaryAliases { get; init; } = Array.Empty<string>();
    public string DisplayName { get; init; } = null!;
    public string Description { get; init; } = null!;
    public string ContentType { get; init; } = null!;
    public string AssetReference { get; init; } = null!;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
    public string Owner { get; init; } = string.Empty;
    public LifecycleState LifecycleState { get; init; } = LifecycleState.Active;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    private Emoji() { }

    public static Result<Emoji> Create(
        EmojiUid uid,
        Alias primaryAlias,
        string displayName,
        string description,
        string contentType,
        string assetReference,
        DateTime createdAt,
        DateTime updatedAt,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? categories = null,
        string? owner = null,
        LifecycleState? state = null,
        IEnumerable<string>? secondaryAliases = null
    )
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Result<Emoji>.Failure(new Error("E300", "Display name must not be empty"));

        if (displayName.Length > 200)
            return Result<Emoji>.Failure(
                new Error("E301", "Display name must be at most 200 characters")
            );

        if (string.IsNullOrWhiteSpace(contentType))
            return Result<Emoji>.Failure(new Error("E302", "Content type must not be empty"));

        if (string.IsNullOrWhiteSpace(assetReference))
            return Result<Emoji>.Failure(new Error("E303", "Asset reference must not be empty"));

        if (createdAt > updatedAt)
            return Result<Emoji>.Failure(
                new Error("E304", "CreatedAt must not be after UpdatedAt")
            );

        var lifecycleState = state ?? LifecycleState.Active;

        var rawTags = PrepareItemList(tags);
        var tagValidation = ValidateItemList(
            rawTags,
            MaxTags,
            MaxTagLength,
            "tag",
            "E305",
            "E306",
            "E307"
        );
        if (tagValidation.IsFailure)
            return Result<Emoji>.Failure(tagValidation.Error!);

        var rawCategories = PrepareItemList(categories);
        var categoryValidation = ValidateItemList(
            rawCategories,
            MaxCategories,
            MaxCategoryLength,
            "category",
            "E308",
            "E309",
            "E310"
        );
        if (categoryValidation.IsFailure)
            return Result<Emoji>.Failure(categoryValidation.Error!);

        var ownerValue = (owner ?? string.Empty).Trim();
        if (ownerValue.Length > MaxOwnerLength)
            return Result<Emoji>.Failure(
                new Error("E311", $"Owner must be at most {MaxOwnerLength} characters")
            );

        var rawSecondary = PrepareItemList(secondaryAliases);
        foreach (var aliasName in rawSecondary)
        {
            var aliasValidation = Alias.Create(aliasName);
            if (aliasValidation.IsFailure)
                return Result<Emoji>.Failure(aliasValidation.Error!);

            if (aliasValidation.Value!.Value == primaryAlias.Value)
                return Result<Emoji>.Failure(
                    new Error(
                        "E312",
                        $"Secondary alias '{aliasName}' must not match the primary alias"
                    )
                );
        }

        return Result<Emoji>.Success(
            new Emoji
            {
                Uid = uid,
                PrimaryAlias = primaryAlias,
                SecondaryAliases = rawSecondary.Distinct().ToList(),
                DisplayName = displayName.Trim(),
                Description = (description ?? string.Empty).Trim(),
                ContentType = contentType.Trim(),
                AssetReference = assetReference.Trim(),
                Tags = rawTags.Distinct().ToList(),
                Categories = rawCategories.Distinct().ToList(),
                Owner = ownerValue,
                LifecycleState = lifecycleState,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            }
        );
    }

    private static IReadOnlyList<string> PrepareItemList(IEnumerable<string>? items)
    {
        if (items is null)
            return Array.Empty<string>();

        return items
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim().ToLowerInvariant())
            .ToList();
    }

    private static Result ValidateItemList(
        IReadOnlyList<string> items,
        int maxCount,
        int maxLength,
        string label,
        string tooManyCode,
        string tooLongCode,
        string invalidFormatCode
    )
    {
        if (items.Count > maxCount)
            return Result.Failure(
                new Error(tooManyCode, $"Too many {label}s (max {maxCount} allowed)")
            );

        foreach (var item in items)
        {
            if (item.Length > maxLength)
                return Result.Failure(
                    new Error(tooLongCode, $"{label} must be at most {maxLength} characters")
                );

            if (!Regex.IsMatch(item, TagOrCategoryCharsPattern))
                return Result.Failure(
                    new Error(
                        invalidFormatCode,
                        $"{label} must start with a letter or digit and contain only lowercase letters, digits, hyphens, and underscores"
                    )
                );
        }

        return Result.Success();
    }
}
