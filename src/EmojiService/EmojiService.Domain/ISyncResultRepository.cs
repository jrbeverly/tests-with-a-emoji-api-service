namespace EmojiService.Domain;

public interface ISyncResultRepository
{
    Task<Result> SaveAsync(SyncResultRecord record);
    Task<SyncResultRecord?> GetByIdAsync(string resultId);
    Task<SyncResultPage> ListByConsumerAsync(string consumerId, string? cursor, int limit);
}
