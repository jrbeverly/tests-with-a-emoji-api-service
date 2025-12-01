using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class ValidateBatchRouteTests : DynamoDbIntegrationTest
{
    public ValidateBatchRouteTests()
        : base("batchv") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    private const string ValidManifest = """
        {
          "items": [
            {
              "file_key": "party",
              "alias": "party-parrot",
              "display_name": "Party Parrot",
              "description": "A festive parrot emoji",
              "tags": ["party", "festive", "fun"],
              "categories": ["fun"],
              "owner": "emoji-team"
            },
            {
              "file_key": "celebrate",
              "alias": "celebrate",
              "display_name": "Celebrate",
              "description": "A celebration emoji",
              "tags": ["celebrate", "happy"],
              "categories": ["reactions"],
              "owner": "emoji-team"
            }
          ]
        }
        """;

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

    // ── Happy path ──

    [Fact]
    public async Task Validate_AllItemsValid_Returns200WithAllValid()
    {
        using var content = CreateMultipartContent(
            ValidManifest,
            ("party", PngBytes, "image/png"),
            ("celebrate", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("manifestId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("validItems").GetInt32().Should().Be(2);
        body.GetProperty("invalidItems").GetInt32().Should().Be(0);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("status").GetString().Should().Be("valid");
        items[1].GetProperty("status").GetString().Should().Be("valid");
    }

    [Fact]
    public async Task Validate_ValidatesWithoutWritingToDynamoDb()
    {
        using var content = CreateMultipartContent(
            ValidManifest,
            ("party", PngBytes, "image/png"),
            ("celebrate", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify aliases were NOT persisted in DynamoDB
        var repo = Factory.Services.GetRequiredService<EmojiService.Domain.IEmojiRepository>();
        var emoji = await repo.GetByAliasAsync("party-parrot");
        emoji.Should().BeNull();
    }

    // ── Manifest format errors ──

    [Fact]
    public async Task Validate_MissingManifest_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "somefile", "somefile.png");

        var response = await Client.PostAsync("/api/v1/batch/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Missing manifest");
    }

    [Fact]
    public async Task Validate_InvalidJsonManifest_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not valid json {{{"), "manifest");

        var response = await Client.PostAsync("/api/v1/batch/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Invalid manifest");
    }

    [Fact]
    public async Task Validate_EmptyItemsArray_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"items":[]}"""), "manifest");

        var response = await Client.PostAsync("/api/v1/batch/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Empty manifest");
    }

    [Fact]
    public async Task Validate_NotMultipart_Returns400()
    {
        var jsonContent = new StringContent("""{"items":[]}""");
        jsonContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );

        var response = await Client.PostAsync("/api/v1/batch/validate", jsonContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validate_TooManyItems_Returns400()
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

        var response = await Client.PostAsync("/api/v1/batch/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Batch too large");
    }

    // ── Per-item validation errors ──

    [Fact]
    public async Task Validate_MissingFileKey_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "alias": "no-file-key", "display_name": "No File Key" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest);

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invalidItems").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues
            .Should()
            .Contain(i =>
                i.GetProperty("code").GetString() == "missing_field"
                && i.GetProperty("field").GetString() == "file_key"
            );
    }

    [Fact]
    public async Task Validate_InvalidAliasFormat_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "bad", "alias": "A", "display_name": "Too Short" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("bad", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("invalidItems").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "alias_invalid");
    }

    [Fact]
    public async Task Validate_MissingFile_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "nonexistent", "alias": "ghost", "display_name": "Ghost" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest);

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "missing_file");
    }

    [Fact]
    public async Task Validate_UnsupportedFileFormat_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "badfmt", "alias": "bad-format", "display_name": "Bad Format" }
              ]
            }
            """;

        var textBytes = "not an image"u8.ToArray();
        using var content = CreateMultipartContent(manifest, ("badfmt", textBytes, "text/plain"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "unsupported_format");
    }

    [Fact]
    public async Task Validate_FileTooLarge_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "bigfile", "alias": "big-file", "display_name": "Big File" }
              ]
            }
            """;

        var bigBytes = new byte[257 * 1024];
        PngBytes.CopyTo(bigBytes, 0);

        using var content = CreateMultipartContent(manifest, ("bigfile", bigBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "file_too_large");
    }

    // ── Alias collision with existing emoji ──

    [Fact]
    public async Task Validate_AliasAlreadyInUse_ReturnsInvalid()
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

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "alias_taken");
    }

    // ── Batch-level duplicate checks ──

    [Fact]
    public async Task Validate_DuplicateAliasesInBatch_ReturnsInvalid()
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

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        var secondItem = items[1];
        secondItem.GetProperty("status").GetString().Should().Be("invalid");
        var issues = secondItem.GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "duplicate_alias");
    }

    [Fact]
    public async Task Validate_DuplicateFileKeysInBatch_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "dupfile", "alias": "first-alias", "display_name": "First" },
                { "file_key": "dupfile", "alias": "second-alias", "display_name": "Second" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("dupfile", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        var secondItem = items[1];
        secondItem.GetProperty("status").GetString().Should().Be("invalid");
        var issues = secondItem.GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "duplicate_file_key");
    }

    // ── Field validation ──

    [Fact]
    public async Task Validate_DisplayNameTooLong_ReturnsInvalid()
    {
        var tooLongName = new string('x', 201);
        var manifest = $$"""
            {
              "items": [
                { "file_key": "longname", "alias": "long-name", "display_name": "{{tooLongName}}" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("longname", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues
            .Should()
            .Contain(i =>
                i.GetProperty("code").GetString() == "field_too_long"
                && i.GetProperty("field").GetString() == "display_name"
            );
    }

    [Fact]
    public async Task Validate_EmptyDisplayName_ReturnsInvalid()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "nodname", "alias": "hasalias", "display_name": "" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("nodname", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("field").GetString() == "display_name");
    }

    [Fact]
    public async Task Validate_TooManyTags_ReturnsInvalid()
    {
        var tooManyTags = Enumerable.Repeat("\"tag\"", Domain.Emoji.MaxTags + 1);
        var tagsStr = string.Join(",", tooManyTags);
        var manifest = $$"""
            {
              "items": [
                { "file_key": "manytags", "alias": "many-tags", "display_name": "Many Tags", "tags": [{{tagsStr}}] }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("manytags", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "too_many_tags");
    }

    [Fact]
    public async Task Validate_TooManyCategories_ReturnsInvalid()
    {
        var tooManyCategories = Enumerable.Repeat("\"cat\"", Domain.Emoji.MaxCategories + 1);
        var catsStr = string.Join(",", tooManyCategories);
        var manifest = $$"""
            {
              "items": [
                { "file_key": "manycats", "alias": "many-cats", "display_name": "Many Cats", "categories": [{{catsStr}}] }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("manycats", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues.Should().Contain(i => i.GetProperty("code").GetString() == "too_many_categories");
    }

    [Fact]
    public async Task Validate_OwnerTooLong_ReturnsInvalid()
    {
        var tooLongOwner = new string('x', Domain.Emoji.MaxOwnerLength + 1);
        var manifest = $$"""
            {
              "items": [
                { "file_key": "longowner", "alias": "long-owner", "display_name": "Long Owner", "owner": "{{tooLongOwner}}" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("longowner", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        issues
            .Should()
            .Contain(i =>
                i.GetProperty("code").GetString() == "field_too_long"
                && i.GetProperty("field").GetString() == "owner"
            );
    }

    // ── Mixed valid and invalid ──

    [Fact]
    public async Task Validate_MixedValidAndInvalid_ReturnsPartialSuccess()
    {
        var manifest = """
            {
              "items": [
                { "file_key": "good", "alias": "good-one", "display_name": "Good One" },
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

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        body.GetProperty("validItems").GetInt32().Should().Be(2);
        body.GetProperty("invalidItems").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("valid");
        items[1].GetProperty("status").GetString().Should().Be("invalid");
        items[2].GetProperty("status").GetString().Should().Be("valid");
    }

    [Fact]
    public async Task Validate_MultipleIssuesPerItem_ReturnsAllIssues()
    {
        // Alias too short + missing file + too many tags + empty display name
        var tooManyTags = Enumerable.Repeat("\"tag\"", Domain.Emoji.MaxTags + 1);
        var tagsStr = string.Join(",", tooManyTags);
        var manifest = $$"""
            {
              "items": [
                { "file_key": "badegg", "alias": "A", "display_name": "", "tags": [{{tagsStr}}] }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest);

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("status").GetString().Should().Be("invalid");
        var issues = items[0].GetProperty("issues").EnumerateArray().ToList();
        var codes = issues.Select(i => i.GetProperty("code").GetString()).ToList();

        codes.Should().Contain("alias_invalid");
        codes.Should().Contain("missing_file");
        codes.Should().Contain("too_many_tags");
        codes.Should().Contain("missing_field"); // empty display_name
    }

    // ── Full batch failure ──

    [Fact]
    public async Task Validate_AllItemsInvalid_ReturnsAllInvalid()
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

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("validItems").GetInt32().Should().Be(0);
        body.GetProperty("invalidItems").GetInt32().Should().Be(2);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().AllSatisfy(i => i.GetProperty("status").GetString().Should().Be("invalid"));
    }

    // ── Response structure ──

    [Fact]
    public async Task Validate_ResponseContainsExpectedFields()
    {
        using var content = CreateMultipartContent(
            ValidManifest,
            ("party", PngBytes, "image/png"),
            ("celebrate", PngBytes, "image/png")
        );

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Top-level fields
        body.GetProperty("manifestId").GetString()!.Length.Should().Be(26);
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("validItems").GetInt32().Should().Be(2);
        body.GetProperty("invalidItems").GetInt32().Should().Be(0);
        body.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);

        // Per-item fields
        var item = body.GetProperty("items")[0];
        item.GetProperty("index").GetInt32().Should().Be(0);
        item.GetProperty("fileKey").GetString().Should().Be("party");
        item.GetProperty("status").GetString().Should().Be("valid");
        // Valid items should have null issues
        item.GetProperty("issues").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Validate_DisplayNameDefaultsToAlias()
    {
        // No display_name provided — should default from alias
        var manifest = """
            {
              "items": [
                { "file_key": "defname", "alias": "my-default-alias" }
              ]
            }
            """;

        using var content = CreateMultipartContent(manifest, ("defname", PngBytes, "image/png"));

        var response = await Client.PostAsync("/api/v1/batch/validate", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("validItems").GetInt32().Should().Be(1);
    }
}
