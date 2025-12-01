namespace EmojiService.Domain;

public interface IAssetStore
{
    Task StoreAsync(string uid, Stream content, string contentType, CancellationToken ct = default);
    Task<AssetStream> GetAsync(string uid, CancellationToken ct = default);
    Task DeleteAsync(string uid, CancellationToken ct = default);
    Task<bool> ExistsAsync(string uid, CancellationToken ct = default);
}

public record AssetStream(Stream Content, string ContentType, long ContentLength);
