using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class SyncOrchestrator : ISyncOrchestrator
{
    private readonly IManifestGenerator _manifestGenerator;
    private readonly IPropagationAdapter _adapter;
    private readonly IConsumerRepository _consumerRepository;
    private readonly ISyncResultRepository _syncResultRepository;

    public SyncOrchestrator(
        IManifestGenerator manifestGenerator,
        IPropagationAdapter adapter,
        IConsumerRepository consumerRepository,
        ISyncResultRepository syncResultRepository
    )
    {
        _manifestGenerator = manifestGenerator;
        _adapter = adapter;
        _consumerRepository = consumerRepository;
        _syncResultRepository = syncResultRepository;
    }

    public async Task<SyncResultRecord> SyncConsumerAsync(Consumer consumer, CancellationToken ct)
    {
        var manifest = _manifestGenerator.Generate(consumer);
        var result = await _adapter.SyncAsync(consumer, manifest, ct);

        var record = new SyncResultRecord
        {
            ResultId = System.Ulid.NewUlid().ToString().ToLowerInvariant(),
            ConsumerId = result.ConsumerId,
            ManifestId = result.ManifestId,
            Status = result.Status.ToString().ToLowerInvariant(),
            TotalEmoji = result.TotalEmoji,
            SyncedEmoji = result.SyncedEmoji,
            SkippedEmoji = result.SkippedEmoji,
            FailedEmoji = result.FailedEmoji,
            ErrorMessage = result.ErrorMessage,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
        };

        await _syncResultRepository.SaveAsync(record);

        return record;
    }

    public async Task NotifyEmojiChangedAsync(CancellationToken ct)
    {
        var allConsumers = await _consumerRepository.ScanAllAsync();

        foreach (var consumer in allConsumers)
        {
            if (consumer.State != Consumer.StateActive)
                continue;

            if (consumer.TriggerMode != Consumer.TriggerModeOnChange)
                continue;

            await SyncConsumerAsync(consumer, ct);
        }
    }
}
