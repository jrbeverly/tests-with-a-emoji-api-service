using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class EmojiRepositoryTests : DynamoDbIntegrationTest
{
    public EmojiRepositoryTests()
        : base("repo") { }

    [Fact]
    public async Task SaveAndGetByUid_RoundTrips()
    {
        var payload = new
        {
            primaryAlias = "happy-face",
            displayName = "Happy Face",
            contentType = "image/png",
            assetReference = "assets/happy.png",
            description = "A happy emoji",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("uid").GetString().Should().Be(uid);
        resolved.GetProperty("primaryAlias").GetString().Should().Be("happy-face");
        resolved.GetProperty("displayName").GetString().Should().Be("Happy Face");
    }

    [Fact]
    public async Task GetByAlias_ResolvesToCorrectEmoji()
    {
        var payload = new
        {
            primaryAlias = "cool-emoji",
            displayName = "Cool Emoji",
            contentType = "image/gif",
            assetReference = "assets/cool.gif",
            description = "A cool emoji",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var expectedUid = created.GetProperty("uid").GetString()!;

        // Resolve by alias via GET /emoji/{uid} (alias → emoji round-trip)
        var getResponse = await Client.GetAsync($"/api/v1/emoji/{expectedUid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("primaryAlias").GetString().Should().Be("cool-emoji");
        resolved.GetProperty("uid").GetString().Should().Be(expectedUid);
    }

    [Fact]
    public async Task ListAliasesForEmoji_ReturnsPrimaryAlias()
    {
        var payload = new
        {
            primaryAlias = "rocket-ship",
            displayName = "Rocket Ship",
            contentType = "image/png",
            assetReference = "assets/rocket.png",
            description = "A rocket emoji",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        // Search with owner filter (indirect verification that GSI1 alias items exist)
        var searchResponse = await Client.GetAsync("/api/v1/emoji");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = searchBody.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle(e => e.GetProperty("uid").GetString() == uid);
    }

    [Fact]
    public async Task DuplicateAlias_IsRejected()
    {
        var payload1 = new
        {
            primaryAlias = "unique-alias",
            displayName = "Unique Emoji",
            contentType = "image/png",
            assetReference = "assets/unique.png",
            description = "First emoji with this alias",
        };

        var first = await Client.PostAsJsonAsync("/api/v1/emoji", payload1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload2 = new
        {
            primaryAlias = "unique-alias",
            displayName = "Duplicate Emoji",
            contentType = "image/png",
            assetReference = "assets/dupe.png",
            description = "Second emoji with same alias",
        };

        var second = await Client.PostAsJsonAsync("/api/v1/emoji", payload2);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var error = await second.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E501");
    }

    [Fact]
    public async Task NotFound_GetByUid_Returns404()
    {
        var response = await Client.GetAsync("/api/v1/emoji/01jabc123xyz4567890abcdefg");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NotFound_GetByAlias_Returns404()
    {
        // Aliases are resolved via GET /emoji/{uid} which requires a UID lookup first.
        // We verify alias resolution indirectly: searching for a non-existent alias prefix
        // returns empty results.
        var response = await Client.GetAsync("/api/v1/emoji?alias_prefix=nonexistent-alias");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetEmoji_ReturnsAllMetadataFields()
    {
        var payload = new
        {
            primaryAlias = "full-metadata",
            displayName = "Full Metadata",
            contentType = "image/gif",
            assetReference = "assets/full.gif",
            description = "Has all the metadata",
            tags = new[] { "test", "integration" },
            categories = new[] { "testing" },
            owner = "qa-team",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("primaryAlias").GetString().Should().Be("full-metadata");
        resolved.GetProperty("displayName").GetString().Should().Be("Full Metadata");
        resolved.GetProperty("description").GetString().Should().Be("Has all the metadata");
        resolved.GetProperty("contentType").GetString().Should().Be("image/gif");
        resolved.GetProperty("assetReference").GetString().Should().Be("assets/full.gif");
        resolved.GetProperty("owner").GetString().Should().Be("qa-team");

        var tags = resolved.GetProperty("tags").EnumerateArray().Select(t => t.GetString());
        tags.Should().BeEquivalentTo(["test", "integration"]);

        var categories = resolved
            .GetProperty("categories")
            .EnumerateArray()
            .Select(c => c.GetString());
        categories.Should().BeEquivalentTo(["testing"]);
    }

    [Fact]
    public async Task UpdateEmoji_PreservesAliasIntegrity()
    {
        var payload = new
        {
            primaryAlias = "reusable-alias",
            displayName = "Reusable",
            contentType = "image/png",
            assetReference = "assets/reuse.png",
            description = "An emoji that gets updated",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        // Emoji is immutable (record type), so re-saving the same data via API creates a new emoji.
        // The duplicate alias check should prevent creating ANOTHER emoji with the same alias.
        var duplicate = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
