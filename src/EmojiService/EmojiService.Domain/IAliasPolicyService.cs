namespace EmojiService.Domain;

public enum AliasPolicyStatus
{
    Allowed,
    Reserved,
    Blocked,
}

public sealed record AliasPolicyResult(AliasPolicyStatus Status, string? Reason);

public interface IAliasPolicyService
{
    AliasPolicyResult CheckName(string name);
}
