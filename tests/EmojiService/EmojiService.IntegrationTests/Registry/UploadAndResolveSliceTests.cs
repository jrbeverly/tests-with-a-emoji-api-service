using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests.Registry;

public class UploadAndResolveSliceTests : DynamoDbIntegrationTest
{
    public UploadAndResolveSliceTests()
        : base("registry") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    // ── Happy path: upload → resolve by UID → resolve by alias → asset byte identity ──

    [Fact]
    public async Task Upload_ThenGetByUidAndAlias_ReturnsEmojiWithIdenticalAssetBytes()
    {
        // Upload
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "emoji.png");
        content.Add(new StringContent("happy-path"), "primaryAlias");
        content.Add(new StringContent("Happy Path Emoji"), "displayName");
        content.Add(new StringContent("Integration test"), "description");
        content.Add(new StringContent("test,integration"), "tags");

        var uploadResponse = await Client.PostAsync("/api/v1/emoji/upload", content);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = uploaded.GetProperty("uid").GetString()!;
        uid.Length.Should().Be(26);
        uploaded.GetProperty("primaryAlias").GetString().Should().Be("happy-path");
        uploaded.GetProperty("assetUrl").GetString().Should().NotBeNullOrEmpty();

        // Verify emoji is persisted (GET by UID)
        var byUidResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        byUidResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var byUid = await byUidResponse.Content.ReadFromJsonAsync<JsonElement>();
        byUid.GetProperty("uid").GetString().Should().Be(uid);
        byUid.GetProperty("primaryAlias").GetString().Should().Be("happy-path");
        byUid.GetProperty("displayName").GetString().Should().Be("Happy Path Emoji");
        byUid.GetProperty("contentType").GetString().Should().Be("image/png");
        byUid.GetProperty("lifecycleState").GetString().Should().Be("active");
        byUid
            .GetProperty("aliases")
            .EnumerateArray()
            .Select(a => a.GetString())
            .Should()
            .BeEquivalentTo(["happy-path"]);
        byUid
            .GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .BeEquivalentTo(["test", "integration"]);
        byUid.GetProperty("assetUrl").GetString().Should().Be($"/assets/{uid}");

        // Verify emoji is resolvable by alias (GET by alias)
        var byAliasResponse = await Client.GetAsync("/api/v1/emoji/by-alias/happy-path");
        byAliasResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var byAlias = await byAliasResponse.Content.ReadFromJsonAsync<JsonElement>();
        byAlias.GetProperty("uid").GetString().Should().Be(uid);
        byAlias.GetProperty("primaryAlias").GetString().Should().Be("happy-path");

        // Verify GET by UID and GET by alias return identical payloads
        var byUidJson = JsonDocument.Parse(byUid.GetRawText()).RootElement;
        var byAliasJson = JsonDocument.Parse(byAlias.GetRawText()).RootElement;
        byUidJson
            .GetProperty("uid")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("uid").GetString());
        byUidJson
            .GetProperty("primaryAlias")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("primaryAlias").GetString());
        byUidJson
            .GetProperty("displayName")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("displayName").GetString());
        byUidJson
            .GetProperty("contentType")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("contentType").GetString());
        byUidJson
            .GetProperty("assetUrl")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("assetUrl").GetString());

        // Verify asset bytes are identical to what was uploaded
        var assetResponse = await Client.GetAsync($"/assets/{uid}");
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        assetResponse.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        var assetBytes = await assetResponse.Content.ReadAsByteArrayAsync();
        assetBytes.Should().Equal(PngBytes);
    }

    // ── Duplicate alias ──

    [Fact]
    public async Task Upload_DuplicateAlias_Returns409Conflict()
    {
        using var content1 = new MultipartFormDataContent();
        var file1 = new ByteArrayContent(PngBytes);
        file1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content1.Add(file1, "file", "first.png");
        content1.Add(new StringContent("dup-alias"), "primaryAlias");
        content1.Add(new StringContent("First Upload"), "displayName");

        var first = await Client.PostAsync("/api/v1/emoji/upload", content1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        using var content2 = new MultipartFormDataContent();
        var file2 = new ByteArrayContent(PngBytes);
        file2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content2.Add(file2, "file", "second.png");
        content2.Add(new StringContent("dup-alias"), "primaryAlias");
        content2.Add(new StringContent("Second Upload"), "displayName");

        var second = await Client.PostAsync("/api/v1/emoji/upload", content2);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Be("E501");
        problem.GetProperty("detail").GetString().Should().Contain("dup-alias");
    }

    // ── Invalid alias ──

    [Fact]
    public async Task Upload_InvalidAlias_Returns400BadRequest()
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(PngBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "emoji.png");
        content.Add(new StringContent("A"), "primaryAlias");
        content.Add(new StringContent("Invalid Alias Test"), "displayName");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem
            .GetProperty("title")
            .GetString()
            .Should()
            .Be("One or more validation errors occurred.");

        problem
            .GetProperty("errors")
            .TryGetProperty("primaryAlias", out var aliasErrors)
            .Should()
            .BeTrue();
        aliasErrors[0].GetString().Should().Contain("at least");
    }

    // ── Missing emoji (UID) ──

    [Fact]
    public async Task GetByUid_UnknownUid_Returns404NotFound()
    {
        var response = await Client.GetAsync("/api/v1/emoji/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain("00000000000000000000000000");
    }

    // ── Missing emoji (alias) ──

    [Fact]
    public async Task GetByAlias_UnknownAlias_Returns404NotFound()
    {
        var response = await Client.GetAsync("/api/v1/emoji/by-alias/nonexistent-alias-xyz");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain("nonexistent-alias-xyz");
    }

    // ── Asset stream bytes ──

    [Fact]
    public async Task GetAsset_ReturnsExactBytesWithCorrectContentType()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "emoji.png");
        content.Add(new StringContent("asset-stream"), "primaryAlias");
        content.Add(new StringContent("Asset Stream Test"), "displayName");

        var uploadResponse = await Client.PostAsync("/api/v1/emoji/upload", content);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = uploaded.GetProperty("uid").GetString()!;

        var assetResponse = await Client.GetAsync($"/assets/{uid}");
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        assetResponse.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        var bytes = await assetResponse.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(PngBytes);
    }

    [Fact]
    public async Task GetAsset_UnknownUid_Returns404NotFound()
    {
        var response = await Client.GetAsync("/assets/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
    }
}
