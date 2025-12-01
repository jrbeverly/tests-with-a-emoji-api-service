using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class SampleIntegrationTests : DynamoDbIntegrationTest
{
    public SampleIntegrationTests()
        : base("sample") { }

    [Fact]
    public async Task CreateAndGet_RoundTripsEmoji()
    {
        var payload = new
        {
            primaryAlias = "test-smile",
            displayName = "Test Smile",
            contentType = "image/png",
            assetReference = "assets/test-smile.png",
            description = "A test smile emoji",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("uid").GetString().Should().Be(uid);
        resolved.GetProperty("primaryAlias").GetString().Should().Be("test-smile");
        resolved.GetProperty("displayName").GetString().Should().Be("Test Smile");
    }
}
