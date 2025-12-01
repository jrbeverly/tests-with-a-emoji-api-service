namespace EmojiService.Domain;

public sealed record Consumer
{
    public const int MaxDisplayNameLength = 200;
    public const string AdapterLocalFs = "local-fs";
    public const string TriggerModeManual = "manual";
    public const string TriggerModeOnChange = "on-change";
    public const string StateActive = "active";
    public const string StatePaused = "paused";
    public const string StateArchived = "archived";

    private static readonly HashSet<string> ValidAdapters = [AdapterLocalFs];
    private static readonly HashSet<string> ValidTriggerModes =
    [
        TriggerModeManual,
        TriggerModeOnChange,
    ];
    private static readonly HashSet<string> ValidStates = [StateActive, StatePaused, StateArchived];

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Adapter { get; init; }
    public required string TargetPath { get; init; }
    public ConsumerSubsetFilter? SubsetFilter { get; init; }
    public required string TriggerMode { get; init; }
    public required string State { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    private Consumer() { }

    public static Result<Consumer> Create(
        string id,
        string displayName,
        string adapter,
        string targetPath,
        DateTime createdAt,
        DateTime updatedAt,
        ConsumerSubsetFilter? subsetFilter = null,
        string triggerMode = TriggerModeManual,
        string state = StateActive
    )
    {
        if (string.IsNullOrWhiteSpace(id))
            return Result<Consumer>.Failure(new Error("E700", "Consumer ID must not be empty"));

        if (string.IsNullOrWhiteSpace(displayName))
            return Result<Consumer>.Failure(new Error("E701", "Display name must not be empty"));

        if (displayName.Length > MaxDisplayNameLength)
            return Result<Consumer>.Failure(
                new Error("E702", $"Display name must be at most {MaxDisplayNameLength} characters")
            );

        if (string.IsNullOrWhiteSpace(adapter))
            return Result<Consumer>.Failure(new Error("E703", "Adapter must not be empty"));

        if (!ValidAdapters.Contains(adapter))
            return Result<Consumer>.Failure(
                new Error("E704", $"Adapter must be one of: {string.Join(", ", ValidAdapters)}")
            );

        if (string.IsNullOrWhiteSpace(targetPath))
            return Result<Consumer>.Failure(new Error("E705", "Target path must not be empty"));

        if (string.IsNullOrWhiteSpace(triggerMode))
            return Result<Consumer>.Failure(new Error("E706", "Trigger mode must not be empty"));

        if (!ValidTriggerModes.Contains(triggerMode))
            return Result<Consumer>.Failure(
                new Error(
                    "E707",
                    $"Trigger mode must be one of: {string.Join(", ", ValidTriggerModes)}"
                )
            );

        if (string.IsNullOrWhiteSpace(state))
            return Result<Consumer>.Failure(new Error("E708", "State must not be empty"));

        if (!ValidStates.Contains(state))
            return Result<Consumer>.Failure(
                new Error("E709", $"State must be one of: {string.Join(", ", ValidStates)}")
            );

        if (createdAt > updatedAt)
            return Result<Consumer>.Failure(
                new Error("E710", "CreatedAt must not be after UpdatedAt")
            );

        var filterValidation = ValidateSubsetFilter(subsetFilter);
        if (filterValidation.IsFailure)
            return Result<Consumer>.Failure(filterValidation.Error!);

        return Result<Consumer>.Success(
            new Consumer
            {
                Id = id,
                DisplayName = displayName.Trim(),
                Adapter = adapter,
                TargetPath = targetPath.Trim(),
                SubsetFilter = subsetFilter,
                TriggerMode = triggerMode,
                State = state,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            }
        );
    }

    public static Result<Consumer> UpdateFrom(
        Consumer existing,
        string displayName,
        string adapter,
        string targetPath,
        string triggerMode,
        string state,
        DateTime updatedAt,
        ConsumerSubsetFilter? subsetFilter = null
    )
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Result<Consumer>.Failure(new Error("E701", "Display name must not be empty"));

        if (displayName.Length > MaxDisplayNameLength)
            return Result<Consumer>.Failure(
                new Error("E702", $"Display name must be at most {MaxDisplayNameLength} characters")
            );

        if (string.IsNullOrWhiteSpace(adapter))
            return Result<Consumer>.Failure(new Error("E703", "Adapter must not be empty"));

        if (!ValidAdapters.Contains(adapter))
            return Result<Consumer>.Failure(
                new Error("E704", $"Adapter must be one of: {string.Join(", ", ValidAdapters)}")
            );

        if (string.IsNullOrWhiteSpace(targetPath))
            return Result<Consumer>.Failure(new Error("E705", "Target path must not be empty"));

        if (string.IsNullOrWhiteSpace(triggerMode))
            return Result<Consumer>.Failure(new Error("E706", "Trigger mode must not be empty"));

        if (!ValidTriggerModes.Contains(triggerMode))
            return Result<Consumer>.Failure(
                new Error(
                    "E707",
                    $"Trigger mode must be one of: {string.Join(", ", ValidTriggerModes)}"
                )
            );

        if (string.IsNullOrWhiteSpace(state))
            return Result<Consumer>.Failure(new Error("E708", "State must not be empty"));

        if (!ValidStates.Contains(state))
            return Result<Consumer>.Failure(
                new Error("E709", $"State must be one of: {string.Join(", ", ValidStates)}")
            );

        if (existing.State == StateArchived)
            return Result<Consumer>.Failure(
                new Error("E720", "Cannot update an archived consumer")
            );

        if (existing.State != state)
        {
            var transitionResult = ValidateStateTransition(existing.State, state);
            if (transitionResult.IsFailure)
                return Result<Consumer>.Failure(transitionResult.Error!);
        }

        var filterValidation = ValidateSubsetFilter(subsetFilter);
        if (filterValidation.IsFailure)
            return Result<Consumer>.Failure(filterValidation.Error!);

        if (updatedAt < existing.UpdatedAt)
            return Result<Consumer>.Failure(
                new Error("E710", "UpdatedAt must not be before existing UpdatedAt")
            );

        return Result<Consumer>.Success(
            existing with
            {
                DisplayName = displayName.Trim(),
                Adapter = adapter,
                TargetPath = targetPath.Trim(),
                SubsetFilter = subsetFilter,
                TriggerMode = triggerMode,
                State = state,
                UpdatedAt = updatedAt,
            }
        );
    }

    public static Result ValidateStateTransition(string from, string to)
    {
        if (from == StateArchived)
            return Result.Failure(
                new Error("E721", "Archived is a terminal state — cannot transition")
            );

        if (from == to)
            return Result.Failure(new Error("E722", $"Already in state '{from}'"));

        var allowed = from switch
        {
            StateActive => new[] { StatePaused, StateArchived },
            StatePaused => new[] { StateActive, StateArchived },
            _ => Array.Empty<string>(),
        };

        if (!allowed.Contains(to))
            return Result.Failure(
                new Error(
                    "E723",
                    $"Cannot transition from '{from}' to '{to}'. Allowed transitions: {string.Join(", ", allowed)}"
                )
            );

        return Result.Success();
    }

    private static Result ValidateSubsetFilter(ConsumerSubsetFilter? filter)
    {
        if (filter is null)
            return Result.Success();

        if (filter.Tags is not null)
        {
            foreach (var tag in filter.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    return Result.Failure(new Error("E730", "SubsetFilter tag must not be empty"));
                if (tag.Length > Emoji.MaxTagLength)
                    return Result.Failure(
                        new Error(
                            "E731",
                            $"SubsetFilter tag must be at most {Emoji.MaxTagLength} characters"
                        )
                    );
            }
        }

        if (filter.Categories is not null)
        {
            foreach (var category in filter.Categories)
            {
                if (string.IsNullOrWhiteSpace(category))
                    return Result.Failure(
                        new Error("E732", "SubsetFilter category must not be empty")
                    );
                if (category.Length > Emoji.MaxCategoryLength)
                    return Result.Failure(
                        new Error(
                            "E733",
                            $"SubsetFilter category must be at most {Emoji.MaxCategoryLength} characters"
                        )
                    );
            }
        }

        if (filter.Owner is not null && filter.Owner.Length == 0)
            return Result.Failure(
                new Error("E734", "SubsetFilter owner must not be empty when specified")
            );

        if (filter.AliasPrefix is not null && filter.AliasPrefix.Length == 0)
            return Result.Failure(
                new Error("E735", "SubsetFilter alias prefix must not be empty when specified")
            );

        if (filter.State is not null)
        {
            var stateResult = LifecycleState.Create(filter.State);
            if (stateResult.IsFailure)
                return Result.Failure(
                    new Error(
                        "E736",
                        $"SubsetFilter state is invalid: {stateResult.Error!.Message}"
                    )
                );
        }

        return Result.Success();
    }
}
