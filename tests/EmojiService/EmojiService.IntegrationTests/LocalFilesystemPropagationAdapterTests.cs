using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmojiService.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class LocalFilesystemPropagationAdapterTests : DynamoDbIntegrationTest
{
    public LocalFilesystemPropagationAdapterTests()
        : base("propagation-adapter") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    [Fact]
    public async Task SyncAsync_HappyPath_WritesManifestAndAssetFiles()
    {
        var uid = await UploadTestEmojiAsync("sync-happy", "Sync Happy");

        var targetDir = CreateTempDir();
        try
        {
            var consumer = await CreateTestConsumerAsync("Happy Path", targetDir);

            var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
            var stored = await repo.GetByIdAsync(consumer.Id);
            stored.Should().NotBeNull();

            var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
            var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

            var manifest = generator.Generate(stored!);
            var result = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);

            result.Status.Should().Be(SyncStatus.Success);
            result.TotalEmoji.Should().Be(1);
            result.SyncedEmoji.Should().Be(1);
            result.FailedEmoji.Should().Be(0);
            result
                .Items.Should()
                .ContainSingle(i => i.EmojiUid == uid && i.Status == SyncItemStatus.Synced);

            var manifestPath = Path.Combine(targetDir, "manifest.json");
            File.Exists(manifestPath).Should().BeTrue();

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            manifestJson.Should().Contain(uid);
            manifestJson.Should().Contain("\"uid\"");
            manifestJson.Should().Contain("\"primary_alias\"");

            var assetPath = Path.Combine(targetDir, "assets", $"{uid}.png");
            File.Exists(assetPath).Should().BeTrue();
            var assetBytes = await File.ReadAllBytesAsync(assetPath);
            assetBytes.Should().Equal(PngBytes);
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task SyncAsync_UnwritableTarget_ReturnsFailed()
    {
        var uid = await UploadTestEmojiAsync("sync-fail", "Sync Fail");

        // /dev/null is a character device file, not a directory.
        // Directory.CreateDirectory will fail trying to create a subdirectory under it.
        var targetDir = "/dev/null/sync-test-unwritable";

        var consumer = await CreateTestConsumerAsync("Unwritable", targetDir);

        var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
        var stored = await repo.GetByIdAsync(consumer.Id);
        stored.Should().NotBeNull();

        var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
        var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

        var manifest = generator.Generate(stored!);
        var result = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);

        result.Status.Should().Be(SyncStatus.Failed);
        result.ErrorMessage.Should().NotBeNull();
        result.SyncedEmoji.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_SecondSync_OverwritesCleanly()
    {
        var uid = await UploadTestEmojiAsync("sync-idempotent", "Sync Idempotent");

        var targetDir = CreateTempDir();
        try
        {
            var consumer = await CreateTestConsumerAsync("Idempotent", targetDir);

            var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
            var stored = await repo.GetByIdAsync(consumer.Id);
            stored.Should().NotBeNull();

            var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
            var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

            var manifest = generator.Generate(stored!);

            // First sync
            var result1 = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);
            result1.Status.Should().Be(SyncStatus.Success);
            result1.TotalEmoji.Should().Be(1);
            result1.SyncedEmoji.Should().Be(1);

            var firstWriteTime = File.GetLastWriteTimeUtc(Path.Combine(targetDir, "manifest.json"));

            // Small delay so file timestamps differ
            await Task.Delay(50);

            // Second sync with same manifest
            var result2 = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);
            result2.Status.Should().Be(SyncStatus.Success);
            result2.TotalEmoji.Should().Be(1);
            result2.SyncedEmoji.Should().Be(1);

            var secondWriteTime = File.GetLastWriteTimeUtc(
                Path.Combine(targetDir, "manifest.json")
            );
            secondWriteTime.Should().BeAfter(firstWriteTime);

            File.Exists(Path.Combine(targetDir, "manifest.json")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "assets", $"{uid}.png")).Should().BeTrue();
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task SyncAsync_MultipleEmoji_AllSynced()
    {
        var uid1 = await UploadTestEmojiAsync("multi-1", "Multi 1");
        var uid2 = await UploadTestEmojiAsync("multi-2", "Multi 2");

        var targetDir = CreateTempDir();
        try
        {
            var consumer = await CreateTestConsumerAsync("Multiple", targetDir);

            var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
            var stored = await repo.GetByIdAsync(consumer.Id);
            stored.Should().NotBeNull();

            var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
            var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

            var manifest = generator.Generate(stored!);
            var result = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);

            result.Status.Should().Be(SyncStatus.Success);
            result.TotalEmoji.Should().Be(2);
            result.SyncedEmoji.Should().Be(2);
            result.FailedEmoji.Should().Be(0);

            File.Exists(Path.Combine(targetDir, "assets", $"{uid1}.png")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "assets", $"{uid2}.png")).Should().BeTrue();

            var manifestJson = await File.ReadAllTextAsync(
                Path.Combine(targetDir, "manifest.json")
            );
            manifestJson.Should().Contain(uid1);
            manifestJson.Should().Contain(uid2);
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task SyncAsync_AdapterDeclaresCapabilities()
    {
        var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();
        var caps = adapter.Capabilities;

        caps.SupportsCreate.Should().BeTrue();
        caps.SupportsUpdate.Should().BeTrue();
        caps.SupportsManifest.Should().BeTrue();
        caps.SupportsAssetFiles.Should().BeTrue();
        caps.SupportsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task SyncAsync_EmptyManifest_WritesEmptyManifest()
    {
        var targetDir = CreateTempDir();
        try
        {
            var consumer = await CreateTestConsumerAsync("Empty", targetDir);

            var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
            var stored = await repo.GetByIdAsync(consumer.Id);
            stored.Should().NotBeNull();

            var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
            var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

            var manifest = generator.Generate(stored!);
            var result = await adapter.SyncAsync(stored!, manifest, CancellationToken.None);

            result.Status.Should().Be(SyncStatus.Success);
            result.TotalEmoji.Should().Be(0);
            result.SyncedEmoji.Should().Be(0);

            var manifestPath = Path.Combine(targetDir, "manifest.json");
            File.Exists(manifestPath).Should().BeTrue();
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task SyncAsync_ManifestWrittenAtomically_NoPartialFileObservable()
    {
        var uid = await UploadTestEmojiAsync("atomic-test", "Atomic Test");

        var targetDir = CreateTempDir();
        try
        {
            var consumer = await CreateTestConsumerAsync("Atomic", targetDir);

            var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
            var stored = await repo.GetByIdAsync(consumer.Id);
            stored.Should().NotBeNull();

            var generator = Factory.Services.GetRequiredService<IManifestGenerator>();
            var adapter = Factory.Services.GetRequiredService<IPropagationAdapter>();

            var manifest = generator.Generate(stored!);
            await adapter.SyncAsync(stored!, manifest, CancellationToken.None);

            // No .tmp files should remain
            var tmpFiles = Directory.GetFiles(targetDir, "*.tmp");
            tmpFiles.Should().BeEmpty();

            // The manifest.json should be the final, complete file
            var manifestPath = Path.Combine(targetDir, "manifest.json");
            File.Exists(manifestPath).Should().BeTrue();
            var content = await File.ReadAllTextAsync(manifestPath);
            content.Should().Contain(uid);
            content.Should().Contain("\"uid\"");
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    // ── Helpers ──

    private async Task<string> UploadTestEmojiAsync(string alias, string displayName)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "test.png");
        content.Add(new StringContent(alias), "primaryAlias");
        content.Add(new StringContent(displayName), "displayName");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("uid").GetString()!;
    }

    private async Task<Consumer> CreateTestConsumerAsync(string displayName, string targetPath)
    {
        var payload = new
        {
            displayName,
            adapter = "local-fs",
            targetPath,
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        var repo = Factory.Services.GetRequiredService<IConsumerRepository>();
        var consumer = await repo.GetByIdAsync(id);
        consumer.Should().NotBeNull();
        return consumer!;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"emoji-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
