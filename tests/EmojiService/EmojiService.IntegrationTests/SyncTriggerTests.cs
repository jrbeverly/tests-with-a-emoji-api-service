using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class SyncTriggerTests : DynamoDbIntegrationTest
{
    public SyncTriggerTests()
        : base("sync-trigger") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    // ── Manual sync ──

    [Fact]
    public async Task ManualSync_ProducesPersistedResultWithCounts()
    {
        await UploadTestEmojiAsync("manual-test", "Manual Test");

        var targetDir = CreateTempDir();
        try
        {
            var consumerId = await CreateConsumerAsync("Manual Sync", targetDir);

            var syncResponse = await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            syncResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var syncBody = await syncResponse.Content.ReadFromJsonAsync<JsonElement>();
            syncBody.GetProperty("status").GetString().Should().Be("success");
            syncBody.GetProperty("totalEmoji").GetInt32().Should().BeGreaterOrEqualTo(1);
            syncBody.GetProperty("syncedEmoji").GetInt32().Should().BeGreaterOrEqualTo(1);
            syncBody.GetProperty("failedEmoji").GetInt32().Should().Be(0);
            syncBody.GetProperty("consumerId").GetString().Should().Be(consumerId);
            syncBody.GetProperty("resultId").GetString().Should().NotBeNullOrEmpty();
            syncBody.GetProperty("manifestId").GetString().Should().NotBeNullOrEmpty();

            var resultId = syncBody.GetProperty("resultId").GetString()!;

            // Verify result is queryable by ID
            var getResultResponse = await Client.GetAsync($"/api/v1/sync-results/{resultId}");
            getResultResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var getBody = await getResultResponse.Content.ReadFromJsonAsync<JsonElement>();
            getBody.GetProperty("resultId").GetString().Should().Be(resultId);
            getBody.GetProperty("consumerId").GetString().Should().Be(consumerId);
            getBody.GetProperty("status").GetString().Should().Be("success");
            getBody.GetProperty("totalEmoji").GetInt32().Should().BeGreaterOrEqualTo(1);
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    // ── Query sync results per consumer ──

    [Fact]
    public async Task ListSyncResults_ReturnsResultsWithPagination()
    {
        var targetDir = CreateTempDir();
        try
        {
            await UploadTestEmojiAsync("list-test", "List Test");

            var consumerId = await CreateConsumerAsync("List Sync", targetDir);

            // Trigger two syncs
            await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);

            var listResponse = await Client.GetAsync(
                $"/api/v1/consumers/{consumerId}/sync-results"
            );
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            var items = listBody.GetProperty("items").EnumerateArray().ToList();
            items.Count.Should().BeGreaterOrEqualTo(2);

            foreach (var item in items)
            {
                item.GetProperty("consumerId").GetString().Should().Be(consumerId);
                item.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
                item.GetProperty("resultId").GetString().Should().NotBeNullOrEmpty();
            }
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task ListSyncResults_PaginationWorks()
    {
        var targetDir = CreateTempDir();
        try
        {
            await UploadTestEmojiAsync("page-test", "Page Test");

            var consumerId = await CreateConsumerAsync("Page Sync", targetDir);

            // Trigger three syncs
            await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);

            // Page size 1 — should get exactly 1 with hasMore=true and cursor
            var page1Response = await Client.GetAsync(
                $"/api/v1/consumers/{consumerId}/sync-results?limit=1"
            );
            page1Response.StatusCode.Should().Be(HttpStatusCode.OK);

            var page1Body = await page1Response.Content.ReadFromJsonAsync<JsonElement>();
            var page1Items = page1Body.GetProperty("items").EnumerateArray().ToList();
            page1Items.Count.Should().Be(1);
            page1Body.GetProperty("hasMore").GetBoolean().Should().BeTrue();
            page1Body.GetProperty("cursor").GetString().Should().NotBeNullOrEmpty();

            var cursor = page1Body.GetProperty("cursor").GetString()!;

            // Fetch page 2 via cursor
            var page2Response = await Client.GetAsync(
                $"/api/v1/consumers/{consumerId}/sync-results?limit=1&cursor={Uri.EscapeDataString(cursor)}"
            );
            page2Response.StatusCode.Should().Be(HttpStatusCode.OK);

            var page2Body = await page2Response.Content.ReadFromJsonAsync<JsonElement>();
            var page2Items = page2Body.GetProperty("items").EnumerateArray().ToList();
            page2Items.Count.Should().Be(1);

            // Different result IDs across pages
            var firstResultId = page1Items[0].GetProperty("resultId").GetString();
            var secondResultId = page2Items[0].GetProperty("resultId").GetString();
            secondResultId.Should().NotBe(firstResultId);
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task ListSyncResults_UnknownConsumer_Returns404()
    {
        var response = await Client.GetAsync(
            "/api/v1/consumers/00000000000000000000000000/sync-results"
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    // ── Get sync result by ID ──

    [Fact]
    public async Task GetSyncResult_UnknownId_Returns404()
    {
        var response = await Client.GetAsync("/api/v1/sync-results/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    // ── Failure recording ──

    [Fact]
    public async Task ManualSync_UnwritableTarget_RecordsFailure()
    {
        await UploadTestEmojiAsync("fail-test", "Fail Test");

        var badPath = "/dev/null/sync-test-unwritable";
        var consumerId = await CreateConsumerAsync("Fail Sync", badPath);

        var syncResponse = await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
        syncResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var syncBody = await syncResponse.Content.ReadFromJsonAsync<JsonElement>();
        syncBody.GetProperty("status").GetString().Should().Be("failed");
        syncBody.GetProperty("syncedEmoji").GetInt32().Should().Be(0);
        syncBody.GetProperty("errorMessage").GetString().Should().NotBeNullOrEmpty();

        var resultId = syncBody.GetProperty("resultId").GetString()!;

        // Verify persisted failure is queryable
        var getResponse = await Client.GetAsync($"/api/v1/sync-results/{resultId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("status").GetString().Should().Be("failed");
        getBody.GetProperty("failedEmoji").GetInt32().Should().BeGreaterOrEqualTo(0);
    }

    // ── On-change sync ──

    [Fact]
    public async Task OnChangeSync_FiresAfterUpload()
    {
        var targetDir = CreateTempDir();
        try
        {
            var consumerId = await CreateOnChangeConsumerAsync("OnChange Sync", targetDir);

            // Upload: should trigger on-change sync
            await UploadTestEmojiAsync("onchange-test", "OnChange Test");

            // Give the fire-and-forget sync time to complete
            await Task.Delay(500);

            // Verify sync results were produced
            var listResponse = await Client.GetAsync(
                $"/api/v1/consumers/{consumerId}/sync-results"
            );
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            var items = listBody.GetProperty("items").EnumerateArray().ToList();
            items.Should().NotBeEmpty("on-change sync should have fired after upload");

            if (items.Count > 0)
            {
                var latest = items[0];
                latest.GetProperty("consumerId").GetString().Should().Be(consumerId);
                latest.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
            }
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    [Fact]
    public async Task TriggerSync_NonExistentConsumer_Returns404()
    {
        var response = await Client.PostAsync(
            "/api/v1/consumers/00000000000000000000000000/sync",
            null
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task TriggerSync_PausedConsumer_Returns409()
    {
        var targetDir = CreateTempDir();
        try
        {
            var consumerId = await CreateConsumerAsync("Paused Sync", targetDir);

            // Pause it
            await Client.PutAsJsonAsync(
                $"/api/v1/consumers/{consumerId}",
                new
                {
                    displayName = "Paused Sync",
                    adapter = "local-fs",
                    targetPath = targetDir,
                    triggerMode = "manual",
                    state = "paused",
                }
            );

            var response = await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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

    private async Task<string> CreateConsumerAsync(string displayName, string targetPath)
    {
        var payload = new
        {
            displayName,
            adapter = "local-fs",
            targetPath,
            triggerMode = "manual",
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateOnChangeConsumerAsync(string displayName, string targetPath)
    {
        var payload = new
        {
            displayName,
            adapter = "local-fs",
            targetPath,
            triggerMode = "on-change",
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"emoji-sync-test-{Guid.NewGuid():N}");
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
