using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmojiService.Domain;

[JsonConverter(typeof(LifecycleStateJsonConverter))]
public sealed record LifecycleState
{
    public static readonly LifecycleState Pending = new("pending");
    public static readonly LifecycleState Active = new("active");
    public static readonly LifecycleState Deprecated = new("deprecated");
    public static readonly LifecycleState Disabled = new("disabled");
    public static readonly LifecycleState Removed = new("removed");

    public static readonly IReadOnlySet<LifecycleState> All = new HashSet<LifecycleState>
    {
        Pending,
        Active,
        Deprecated,
        Disabled,
        Removed,
    };

    public static readonly IReadOnlySet<LifecycleState> Resolvable = new HashSet<LifecycleState>
    {
        Active,
        Deprecated,
    };

    private static readonly Dictionary<LifecycleState, HashSet<LifecycleState>> Transitions = new()
    {
        [Pending] = [Active, Removed],
        [Active] = [Deprecated, Disabled],
        [Deprecated] = [Active, Disabled],
        [Disabled] = [Active, Removed],
        [Removed] = [],
    };

    public string Value { get; }

    private LifecycleState(string value) => Value = value;

    public static Result<LifecycleState> Create(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();

        foreach (var state in All)
        {
            if (state.Value == normalized)
                return Result<LifecycleState>.Success(state);
        }

        return Result<LifecycleState>.Failure(
            new Error(
                "E400",
                $"Invalid lifecycle state '{normalized}'. Valid states: {string.Join(", ", All.Select(s => s.Value))}"
            )
        );
    }

    public IReadOnlySet<LifecycleState> GetAllowedTransitions()
    {
        return Transitions.TryGetValue(this, out var allowed)
            ? allowed
            : new HashSet<LifecycleState>();
    }

    public static Result ValidateTransition(LifecycleState from, LifecycleState to)
    {
        if (from == to)
            return Result.Failure(new Error("E410", $"Already in state '{from.Value}'"));

        if (!Transitions.TryGetValue(from, out var allowed) || !allowed.Contains(to))
        {
            var allowedNames = from.GetAllowedTransitions().Select(s => s.Value).OrderBy(n => n);
            return Result.Failure(
                new Error(
                    "E410",
                    $"Cannot transition from '{from.Value}' to '{to.Value}'. "
                        + $"Allowed transitions: {string.Join(", ", allowedNames)}"
                )
            );
        }

        return Result.Success();
    }

    public override string ToString() => Value;
}

public sealed class LifecycleStateJsonConverter : JsonConverter<LifecycleState>
{
    public override LifecycleState? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var value = reader.GetString();
        if (value is null)
            return null;

        var result = LifecycleState.Create(value);
        return result.IsSuccess ? result.Value : null;
    }

    public override void Write(
        Utf8JsonWriter writer,
        LifecycleState value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.Value);
    }
}
