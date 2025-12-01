namespace EmojiService.Domain;

public interface ISyncOrchestrator
{
    Task<SyncResultRecord> SyncConsumerAsync(Consumer consumer, CancellationToken ct);
    Task NotifyEmojiChangedAsync(CancellationToken ct);
}
