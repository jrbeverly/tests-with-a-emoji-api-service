using System.Text.RegularExpressions;

namespace EmojiService.Domain;

public sealed record Alias
{
    private const string ValidPattern = @"^[a-z0-9][a-z0-9_-]+$";
    public const int MinLength = 2;
    public const int MaxLength = 50;

    public string Value { get; }

    private Alias(string value)
    {
        Value = value;
    }

    public static Result<Alias> Create(string name)
    {
        if (string.IsNullOrEmpty(name))
            return Result<Alias>.Failure(new Error("E200", "Alias must not be empty"));

        var normalized = name.Trim().ToLowerInvariant();

        if (normalized.Length < MinLength)
            return Result<Alias>.Failure(
                new Error("E201", $"Alias must be at least {MinLength} characters")
            );

        if (normalized.Length > MaxLength)
            return Result<Alias>.Failure(
                new Error("E202", $"Alias must be at most {MaxLength} characters")
            );

        if (!Regex.IsMatch(normalized, ValidPattern))
            return Result<Alias>.Failure(
                new Error(
                    "E203",
                    "Alias must start with a letter or digit and contain only lowercase letters, digits, hyphens, and underscores"
                )
            );

        return Result<Alias>.Success(new Alias(normalized));
    }

    public override string ToString() => Value;
}
