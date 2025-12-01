namespace EmojiService.Domain;

public interface IEmojiRepository
{
    Task<Result> SaveAsync(Emoji emoji);
    Task<Emoji?> GetByUidAsync(EmojiUid uid);
    Task<Emoji?> GetByAliasAsync(string aliasName);
    Task<IReadOnlyList<AliasRecord>> ListAliasesForEmojiAsync(string uid);
    Task<IReadOnlyList<Emoji>> ScanAllAsync();
    Task<Result> AddAliasAsync(EmojiUid emojiUid, string aliasName);
    Task<Result> RetireAliasAsync(EmojiUid emojiUid, string aliasName);
    Task<Result> SetPrimaryAliasAsync(EmojiUid emojiUid, string aliasName);
    Task<Result> RemapAliasAsync(string aliasName, EmojiUid targetEmojiUid);
    Task<Result> RemapAliasWithOverrideAsync(string aliasName, EmojiUid targetEmojiUid);
}

public record AliasRecord
{
    public required string Name { get; init; }
    public required string EmojiUid { get; init; }
}
