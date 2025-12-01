using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class GetEmojiAndAssetTests : DynamoDbIntegrationTest
{
    public GetEmojiAndAssetTests()
        : base("get-emoji-asset") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    // ── Get by UID ──

    [Fact]
    public async Task GetByUid_ReturnsEmojiWithAssetUrl()
    {
        // Seed an emoji
        var payload = new
        {
            primaryAlias = "getuid-test",
            displayName = "Get UID Test",
            contentType = "image/png",
            assetReference = "assets/test.png",
            description = "Test emoji for get-by-uid",
            tags = new[] { "test" },
            categories = new[] { "qa" },
            owner = "test-team",
        };
        await SeedEmojiAsync(payload);

        var search = await Client.GetFromJsonAsync<JsonElement>("/api/v1/emoji?tag=test");
        var uid = search.GetProperty("items")[0].GetProperty("uid").GetString()!;

        var response = await Client.GetAsync($"/api/v1/emoji/{uid}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("uid").GetString().Should().Be(uid);
        body.GetProperty("primaryAlias").GetString().Should().Be("getuid-test");
        body.GetProperty("displayName").GetString().Should().Be("Get UID Test");
        body.GetProperty("contentType").GetString().Should().Be("image/png");
        body.GetProperty("assetReference").GetString().Should().Be("assets/test.png");
        body.GetProperty("assetUrl").GetString().Should().Be($"/assets/{uid}");
        body.GetProperty("lifecycleState").GetString().Should().Be("active");
        body.GetProperty("aliases")
            .EnumerateArray()
            .Select(a => a.GetString())
            .Should()
            .BeEquivalentTo(["getuid-test"]);
        body.GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .BeEquivalentTo(["test"]);
        body.GetProperty("categories")
            .EnumerateArray()
            .Select(c => c.GetString())
            .Should()
            .BeEquivalentTo(["qa"]);
        body.GetProperty("owner").GetString().Should().Be("test-team");
        body.GetProperty("createdAt").GetDateTime().Should().BeAfter(DateTime.MinValue);
        body.GetProperty("updatedAt").GetDateTime().Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task GetByUid_UnknownUid_Returns404WithProblemDetails()
    {
        var response = await Client.GetAsync("/api/v1/emoji/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain("00000000000000000000000000");
    }

    [Fact]
    public async Task GetByUid_InvalidUid_Returns400WithProblemDetails()
    {
        var response = await Client.GetAsync("/api/v1/emoji/not-a-valid-uid");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("title").GetString().Should().BeOneOf(["E101", "E103"]);
    }

    // ── Get by alias ──

    [Fact]
    public async Task GetByAlias_ReturnsEmojiWithAssetUrl()
    {
        var payload = new
        {
            primaryAlias = "byalias-test",
            displayName = "ByAlias Test",
            contentType = "image/gif",
            assetReference = "assets/alias.gif",
            description = "Test emoji for get-by-alias",
        };
        await SeedEmojiAsync(payload);

        var response = await Client.GetAsync("/api/v1/emoji/by-alias/byalias-test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("primaryAlias").GetString().Should().Be("byalias-test");
        body.GetProperty("displayName").GetString().Should().Be("ByAlias Test");
        body.GetProperty("contentType").GetString().Should().Be("image/gif");
        body.GetProperty("uid").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("assetUrl").GetString().Should().Contain("/assets/");
        body.GetProperty("lifecycleState").GetString().Should().Be("active");
    }

    [Fact]
    public async Task GetByAlias_UnknownAlias_Returns404WithProblemDetails()
    {
        var response = await Client.GetAsync("/api/v1/emoji/by-alias/nonexistent-alias");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain("nonexistent-alias");
    }

    [Fact]
    public async Task GetByAlias_InvalidAlias_Returns400WithProblemDetails()
    {
        var response = await Client.GetAsync("/api/v1/emoji/by-alias/X");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("title").GetString().Should().BeOneOf(["E201", "E203"]);
    }

    // ── Identical payloads ──

    [Fact]
    public async Task GetByUidAndGetByAlias_ReturnIdenticalPayloads()
    {
        var payload = new
        {
            primaryAlias = "identical-test",
            displayName = "Identical Test",
            contentType = "image/png",
            assetReference = "assets/identical.png",
            description = "Testing identical payloads",
            tags = new[] { "identical" },
            categories = new[] { "qa" },
            owner = "test-owner",
        };
        await SeedEmojiAsync(payload);

        var search = await Client.GetFromJsonAsync<JsonElement>("/api/v1/emoji?tag=identical");
        var uid = search.GetProperty("items")[0].GetProperty("uid").GetString()!;

        var byUidResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        var byAliasResponse = await Client.GetAsync("/api/v1/emoji/by-alias/identical-test");

        byUidResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        byAliasResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var byUidBody = await byUidResponse.Content.ReadAsStringAsync();
        var byAliasBody = await byAliasResponse.Content.ReadAsStringAsync();

        var byUidJson = JsonDocument.Parse(byUidBody).RootElement;
        var byAliasJson = JsonDocument.Parse(byAliasBody).RootElement;

        // Both payloads should have the same property names and values
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
        byUidJson
            .GetProperty("assetReference")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("assetReference").GetString());
        byUidJson
            .GetProperty("lifecycleState")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("lifecycleState").GetString());
        byUidJson
            .GetProperty("owner")
            .GetString()
            .Should()
            .Be(byAliasJson.GetProperty("owner").GetString());
    }

    // ── Asset streaming ──

    [Fact]
    public async Task GetAsset_StreamsBytesWithCorrectContentType()
    {
        // Upload an emoji with actual file bytes
        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        uploadContent.Add(fileContent, "file", "test.png");
        uploadContent.Add(new StringContent("asset-test"), "primaryAlias");
        uploadContent.Add(new StringContent("Asset Test"), "displayName");

        var uploadResponse = await Client.PostAsync("/api/v1/emoji/upload", uploadContent);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = uploaded.GetProperty("uid").GetString()!;

        // Stream the asset
        var assetResponse = await Client.GetAsync($"/assets/{uid}");
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        assetResponse.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        var bytes = await assetResponse.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(PngBytes);
    }

    [Fact]
    public async Task GetAsset_UnknownEmoji_Returns404WithProblemDetails()
    {
        var response = await Client.GetAsync("/assets/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
    }

    [Fact]
    public async Task GetAsset_EmojiWithoutFile_Returns404WithProblemDetails()
    {
        // Create emoji via POST /emoji (no asset file stored)
        var payload = new
        {
            primaryAlias = "no-asset-test",
            displayName = "No Asset Test",
            contentType = "image/png",
            assetReference = "assets/missing.png",
        };
        await SeedEmojiAsync(payload);

        var getResponse = await Client.GetAsync("/api/v1/emoji/by-alias/no-asset-test");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = body.GetProperty("uid").GetString()!;

        var response = await Client.GetAsync($"/assets/{uid}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain("Asset not found");
    }
}
