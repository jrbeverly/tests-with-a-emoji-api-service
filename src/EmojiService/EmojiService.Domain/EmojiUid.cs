using System.Text.RegularExpressions;

namespace EmojiService.Domain;

public sealed record EmojiUid
{
    private const string ValidCharsPattern = @"^[0123456789abcdefghjkmnpqrstvwxyz]{26}$";

    public string Value { get; }

    private EmojiUid(string value)
    {
        Value = value;
    }

    public static Result<EmojiUid> Create(string value)
    {
        if (string.IsNullOrEmpty(value))
            return Result<EmojiUid>.Failure(new Error("E100", "Emoji UID must not be empty"));

        if (value.Length != 26)
            return Result<EmojiUid>.Failure(
                new Error("E101", "Emoji UID must be exactly 26 characters")
            );

        if (value != value.ToLowerInvariant())
            return Result<EmojiUid>.Failure(new Error("E102", "Emoji UID must be lowercase"));

        if (!Regex.IsMatch(value, ValidCharsPattern))
            return Result<EmojiUid>.Failure(
                new Error("E103", "Emoji UID contains invalid characters")
            );

        return Result<EmojiUid>.Success(new EmojiUid(value));
    }

    public override string ToString() => Value;
}
