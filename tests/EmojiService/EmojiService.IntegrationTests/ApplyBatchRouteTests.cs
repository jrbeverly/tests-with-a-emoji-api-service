using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class ApplyBatchRouteTests : DynamoDbIntegrationTest
{
    public ApplyBatchRouteTests()
        : base("batcha") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    private static MultipartFormDataContent CreateMultipartContent(
        string manifestJson,
        params (string Key, byte[] Bytes, string ContentType)[] files
    )
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(manifestJson), "manifest");

        foreach (var (key, bytes, mimeType) in files)
        {
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                mimeType
            );
            content.Add(fileContent, key, $"{key}.png");
        }

        return content;
    }

    private const string TwoItemManifest = """
        {
          "items": [
            {
              "file_key": "first",
              "alias": "first-emoji",
              "display_name": "First Emoji",
              "description": "The first one",
              "tags": ["alpha", "first"],
              "categories": ["test"],
              "owner": "test-team"
            },
            {
              "file_key": "second",
              "alias": "second-emoji",
              "display_name": "Second Emoji",
              "description": "The second one",
              "tags": ["beta"],
              "categories": ["test"],
              "owner": "test-team"
            }
          ]
        }
        """;

    // ── Full success ──

    [Fact]
    public async Task Apply_AllItemsValid_PersistsAllAndReturnsCreated()
    {
        using var content = CreateMultipartContent(
            TwoItemManifest,
            ("first", PngBytes, "image/png"),
            ("second", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("manifestId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("succeeded").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(0);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("status").GetString().Should().Be("created");
        items[0].GetProperty("emojiUid").GetString().Should().NotBeNullOrEmpty();
        items[1].GetProperty("status").GetString().Should().Be("created");
        items[1].GetProperty("emojiUid").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Apply_AllItemsValid_EmojiResolvableIndividually()
    {
        using var content = CreateMultipartContent(
            TwoItemManifest,
            ("first", PngBytes, "image/png"),
            ("second", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var uid = body.GetProperty("items")[0].GetProperty("emojiUid").GetString()!;

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Apply_AllItemsValid_WritesBatchAuditEvent()
    {
        using var content = CreateMultipartContent(
            TwoItemManifest,
            ("first", PngBytes, "image/png"),
            ("second", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var manifestId = body.GetProperty("manifestId").GetString()!;

        var auditLog = Factory.Services.GetRequiredService<EmojiService.Domain.IAuditLog>();
        var recentPage = await auditLog.GetRecentAsync(20, null);

        recentPage
            .Items.Should()
            .Contain(e =>
                e.Action == "emoji.batch_apply" && e.SubjectUid == $"MANIFEST#{manifestId}"
            );
    }

    // ── Partial success ──

    [Fact]
    public async Task Apply_MixedValidAndInvalid_ReturnsPartialSuccess()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "good", "alias": "good-emoji", "display_name": "Good" },
                { "file_key": "bad", "alias": "A", "display_name": "Bad One" },
                { "file_key": "also-good", "alias": "also-good", "display_name": "Also Good" }
              ]
            }
            """;

        using var content = CreateMultipartContent(
            manifest,
            ("good", PngBytes, "image/png"),
            ("bad", PngBytes, "image/png"),
            ("also-good", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        body.GetProperty("succeeded").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("created");
        items[0].GetProperty("emojiUid").GetString().Should().NotBeNullOrEmpty();
        items[1].GetProperty("status").GetString().Should().Be("skipped");
        items[1]
            .GetProperty("issues")
            .EnumerateArray()
            .ToList()
            .Should()
            .Contain(i => i.GetProperty("code").GetString() == "alias_invalid");
        items[2].GetProperty("status").GetString().Should().Be("created");
    }

    [Fact]
    public async Task Apply_SkippedItemsNotPersisted()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "good", "alias": "good-emoji", "display_name": "Good" },
                { "file_key": "bad", "alias": "A", "display_name": "Bad" }
              ]
            }
            """;

        using var content = CreateMultipartContent(
            manifest,
            ("good", PngBytes, "image/png"),
            ("bad", PngBytes, "image/png")
        );

        await Client.PostAsync("/api/v1/batch/apply", content);

        var repo = Factory.Services.GetRequiredService<EmojiService.Domain.IEmojiRepository>();
        var badEmoji = await repo.GetByAliasAsync("A");
        badEmoji.Should().BeNull();
    }

    // ── Full failure ──

    [Fact]
    public async Task Apply_AllItemsInvalid_ReturnsAllSkipped()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "missing1", "alias": "A", "display_name": "" },
                { "file_key": "missing2", "alias": "B", "display_name": "" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest);

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("succeeded").GetInt32().Should().Be(0);
        body.GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(2);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().AllSatisfy(i => i.GetProperty("status").GetString().Should().Be("skipped"));
    }

    [Fact]
    public async Task Apply_AllItemsInvalid_WritesBatchAuditEventWithZeroSucceeded()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "bad1", "alias": "A", "display_name": "" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest);

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var manifestId = body.GetProperty("manifestId").GetString()!;

        var auditLog = Factory.Services.GetRequiredService<EmojiService.Domain.IAuditLog>();
        var recentPage = await auditLog.GetRecentAsync(20, null);

        recentPage
            .Items.Should()
            .Contain(e =>
                e.Action == "emoji.batch_apply" && e.SubjectUid == $"MANIFEST#{manifestId}"
            );
    }

    // ── Manifest format errors ──

    [Fact]
    public async Task Apply_MissingManifest_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "somefile", "somefile.png");

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_InvalidJsonManifest_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not valid json {{{"), "manifest");

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_EmptyItemsArray_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"items":[]}"""), "manifest");

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_NotMultipart_Returns400()
    {
        var jsonContent = new StringContent("""{"items":[]}""");
        jsonContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", jsonContent);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_TooManyItems_Returns400()
    {
        var largeManifest = new System.Text.StringBuilder();
        largeManifest.Append("""{"items":[""");
        for (var i = 0; i < 101; i++)
        {
            if (i > 0)
                largeManifest.Append(',');
            largeManifest.Append(
                $$"""{"file_key":"fk{{i}}","alias":"a{{i}}","display_name":"Item {{i}}"}"""
            );
        }
        largeManifest.Append("]}");

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(largeManifest.ToString()), "manifest");

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Duplicate within batch ──

    [Fact]
    public async Task Apply_DuplicateAliasesInBatch_SkipsDuplicate()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "first", "alias": "dup-alias", "display_name": "First" },
                { "file_key": "second", "alias": "dup-alias", "display_name": "Second" }
              ]
            }
            """;

        using var content = CreateMultipartContent(
            manifest,
            ("first", PngBytes, "image/png"),
            ("second", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("succeeded").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("created");
        items[1].GetProperty("status").GetString().Should().Be("skipped");
        items[1]
            .GetProperty("issues")
            .EnumerateArray()
            .ToList()
            .Should()
            .Contain(i => i.GetProperty("code").GetString() == "duplicate_alias");
    }

    // ── Alias collision with existing emoji ──

    [Fact]
    public async Task Apply_AliasAlreadyInUse_SkipsItem()
    {
        await SeedEmojiAsync(
            new
            {
                primaryAlias = "existing-alias",
                displayName = "Existing",
                contentType = "image/png",
                assetReference = "assets/existing.png",
            }
        );

        var manifest = """
            {
              "items": [
                { "file_key": "collision", "alias": "existing-alias", "display_name": "Collision" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("collision", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("succeeded").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("skipped");
        items[0]
            .GetProperty("issues")
            .EnumerateArray()
            .ToList()
            .Should()
            .Contain(i => i.GetProperty("code").GetString() == "alias_taken");
    }

    // ── Response structure ──

    [Fact]
    public async Task Apply_ResponseContainsExpectedFields()
    {
        using var content = CreateMultipartContent(
            TwoItemManifest,
            ("first", PngBytes, "image/png"),
            ("second", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("manifestId").GetString()!.Length.Should().Be(26);
        body.GetProperty("appliedAt").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("succeeded").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(0);

        var item = body.GetProperty("items")[0];
        item.GetProperty("index").GetInt32().Should().Be(0);
        item.GetProperty("fileKey").GetString().Should().Be("first");
        item.GetProperty("status").GetString().Should().Be("created");
        item.GetProperty("emojiUid").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("issues").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Apply_SkippedItemHasIssues()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "bad", "alias": "A", "display_name": "" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("bad", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var item = body.GetProperty("items")[0];
        item.GetProperty("status").GetString().Should().Be("skipped");
        item.GetProperty("issues").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── Display name defaults to alias ──

    [Fact]
    public async Task Apply_DisplayNameDefaultsToAlias()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "defname", "alias": "my-default-alias" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("defname", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/apply", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("succeeded").GetInt32().Should().Be(1);
    }
}
