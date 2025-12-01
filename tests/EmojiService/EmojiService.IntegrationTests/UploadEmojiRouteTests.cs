using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class UploadEmojiRouteTests : DynamoDbIntegrationTest
{
    public UploadEmojiRouteTests()
        : base("upload") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    [Fact]
    public async Task UploadEmoji_ValidFileAndMetadata_ReturnsCreated()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "emoji.png");
        content.Add(new StringContent("test-upload"), "primaryAlias");
        content.Add(new StringContent("Test Upload"), "displayName");
        content.Add(new StringContent("A test emoji"), "description");
        content.Add(new StringContent("demo,test"), "tags");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("uid").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("uid").GetString()!.Length.Should().Be(26);
        body.GetProperty("primaryAlias").GetString().Should().Be("test-upload");
        body.GetProperty("assetUrl").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("createdAt").GetDateTime().Should().BeAfter(DateTime.MinValue);

        // Verify the emoji is resolvable by UID
        var uid = body.GetProperty("uid").GetString()!;
        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("primaryAlias").GetString().Should().Be("test-upload");
        resolved.GetProperty("contentType").GetString().Should().Be("image/png");
        resolved
            .GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .BeEquivalentTo(["demo", "test"]);
    }

    [Fact]
    public async Task UploadEmoji_DuplicateAlias_ReturnsConflict()
    {
        // First upload
        using var content1 = new MultipartFormDataContent();
        var fileContent1 = new ByteArrayContent(PngBytes);
        fileContent1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content1.Add(fileContent1, "file", "emoji.png");
        content1.Add(new StringContent("dup-alias"), "primaryAlias");
        content1.Add(new StringContent("First"), "displayName");

        var first = await Client.PostAsync("/api/v1/emoji/upload", content1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Second upload with same alias
        using var content2 = new MultipartFormDataContent();
        var fileContent2 = new ByteArrayContent(PngBytes);
        fileContent2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content2.Add(fileContent2, "file", "emoji.png");
        content2.Add(new StringContent("dup-alias"), "primaryAlias");
        content2.Add(new StringContent("Second"), "displayName");

        var second = await Client.PostAsync("/api/v1/emoji/upload", content2);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Be("E501");
        problem.GetProperty("detail").GetString().Should().Contain("dup-alias");
    }

    [Fact]
    public async Task UploadEmoji_InvalidAlias_ReturnsBadRequestWithFieldErrors()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "emoji.png");
        content.Add(new StringContent("A"), "primaryAlias");
        content.Add(new StringContent("Bad Alias"), "displayName");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem
            .GetProperty("title")
            .GetString()
            .Should()
            .Be("One or more validation errors occurred.");

        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("primaryAlias", out var aliasErrors).Should().BeTrue();
        aliasErrors[0].GetString().Should().Contain("at least");
    }

    [Fact]
    public async Task UploadEmoji_UnsupportedContentType_ReturnsBadRequestWithFieldErrors()
    {
        var textBytes = "not an image"u8.ToArray();
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(textBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "text/plain"
        );
        content.Add(fileContent, "file", "text.txt");
        content.Add(new StringContent("text-file"), "primaryAlias");
        content.Add(new StringContent("Text File Upload"), "displayName");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem
            .GetProperty("title")
            .GetString()
            .Should()
            .Be("One or more validation errors occurred.");

        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("file", out var fileErrors).Should().BeTrue();
        fileErrors[0].GetString().Should().Contain("text/plain");
    }
}
