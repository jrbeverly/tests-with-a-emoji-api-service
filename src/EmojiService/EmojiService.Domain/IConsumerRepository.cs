namespace EmojiService.Domain;

public interface IConsumerRepository
{
    Task<Result> SaveAsync(Consumer consumer);
    Task<Consumer?> GetByIdAsync(string id);
    Task<IReadOnlyList<Consumer>> ScanAllAsync();
    Task<Result> DeleteAsync(string id);
}
