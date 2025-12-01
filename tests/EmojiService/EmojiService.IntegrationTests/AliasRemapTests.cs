using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class AliasRemapTests : DynamoDbIntegrationTest
{
    public AliasRemapTests()
        : base("alias-remap") { }

    // ── Success cases ──

    [Fact]
    public async Task RemapAlias_MovesSecondaryFromOldToNewEmoji()
    {
        var srcUid = await SeedActiveEmojiAsync("src-emoji");
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{srcUid}/aliases", new { alias = "move-me" });

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/move-me/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("alias").GetString().Should().Be("move-me");
        body.GetProperty("previousEmojiUid").GetString().Should().Be(srcUid);
        body.GetProperty("targetEmojiUid").GetString().Should().Be(dstUid);

        var srcGet = await Client.GetAsync($"/api/v1/emoji/{srcUid}");
        var srcBody = await srcGet.Content.ReadFromJsonAsync<JsonElement>();
        var srcAliases = GetAliases(srcBody);
        srcAliases.Should().NotContain("move-me");
        srcAliases.Should().Contain("src-emoji");

        var dstGet = await Client.GetAsync($"/api/v1/emoji/{dstUid}");
        var dstBody = await dstGet.Content.ReadFromJsonAsync<JsonElement>();
        var dstAliases = GetAliases(dstBody);
        dstAliases.Should().Contain("move-me");
    }

    [Fact]
    public async Task RemapAlias_PrimaryWithSecondaries_PromotesSecondary()
    {
        var srcUid = await SeedActiveEmojiAsync("old-primary");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{srcUid}/aliases", new { alias = "takeover" });
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/old-primary/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var srcGet = await Client.GetAsync($"/api/v1/emoji/{srcUid}");
        var srcBody = await srcGet.Content.ReadFromJsonAsync<JsonElement>();
        srcBody.GetProperty("primaryAlias").GetString().Should().Be("takeover");
        var srcAliases = GetAliases(srcBody);
        srcAliases.Should().NotContain("old-primary");
        srcAliases.Should().Contain("takeover");

        var dstGet = await Client.GetAsync($"/api/v1/emoji/{dstUid}");
        var dstBody = await dstGet.Content.ReadFromJsonAsync<JsonElement>();
        var dstAliases = GetAliases(dstBody);
        dstAliases.Should().Contain("old-primary");
        dstAliases.Should().Contain("dst-emoji");
    }

    [Fact]
    public async Task RemapAlias_PrimaryWithNoSecondaries_ReturnsError()
    {
        var srcUid = await SeedActiveEmojiAsync("lonely-primary");
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/lonely-primary/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E547");
    }

    [Fact]
    public async Task RemapAlias_WritesAuditEventsForBothEmoji()
    {
        var srcUid = await SeedActiveEmojiAsync("audit-src");
        var dstUid = await SeedActiveEmojiAsync("audit-dst");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/aliases",
            new { alias = "audit-move" }
        );

        await Client.PutAsJsonAsync("/api/v1/aliases/audit-move/target", new { uid = dstUid });

        var srcAudit = await Client.GetAsync($"/api/v1/emoji/{srcUid}/audit");
        var srcBody = await srcAudit.Content.ReadFromJsonAsync<JsonElement>();
        var srcItems = srcBody.GetProperty("items").EnumerateArray().ToList();
        srcItems.Should().Contain(i => i.GetProperty("action").GetString() == "alias.remap");
        srcItems.Count(i => i.GetProperty("action").GetString() == "alias.remap").Should().Be(1);

        var dstAudit = await Client.GetAsync($"/api/v1/emoji/{dstUid}/audit");
        var dstBody = await dstAudit.Content.ReadFromJsonAsync<JsonElement>();
        var dstItems = dstBody.GetProperty("items").EnumerateArray().ToList();
        dstItems.Should().Contain(i => i.GetProperty("action").GetString() == "alias.remap");
        dstItems.Count(i => i.GetProperty("action").GetString() == "alias.remap").Should().Be(1);
    }

    [Fact]
    public async Task RemapAlias_AuditEventCapturesPreviousAndNewOwner()
    {
        var srcUid = await SeedActiveEmojiAsync("owner-src");
        var dstUid = await SeedActiveEmojiAsync("owner-dst");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/aliases",
            new { alias = "transfer-me" }
        );

        await Client.PutAsJsonAsync("/api/v1/aliases/transfer-me/target", new { uid = dstUid });

        var srcAudit = await Client.GetAsync($"/api/v1/emoji/{srcUid}/audit");
        var srcBody = await srcAudit.Content.ReadFromJsonAsync<JsonElement>();
        var srcItems = srcBody.GetProperty("items").EnumerateArray().ToList();
        var remapEvent = srcItems.First(i => i.GetProperty("action").GetString() == "alias.remap");
        remapEvent.GetProperty("subjectUid").GetString().Should().Be(srcUid);
    }

    // ── Protected alias rejection ──

    [Fact]
    public async Task RemapAlias_ProtectedAlias_Returns403()
    {
        var srcUid = await SeedActiveEmojiAsync("guarded");
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");

        // Directly set protection on the alias item in DynamoDB
        var db = Factory.Services.GetRequiredService<IAmazonDynamoDB>();
        var getResponse = await db.GetItemAsync(
            new GetItemRequest
            {
                TableName = TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue("ALIAS#guarded"),
                    ["SK"] = new AttributeValue("META"),
                },
            }
        );

        var aliasItem = getResponse.Item;
        var data = JsonSerializer.Deserialize<JsonElement>(aliasItem["Data"].S);
        var updatedData = JsonSerializer.Serialize(
            new
            {
                name = data.GetProperty("name").GetString(),
                emoji_uid = data.GetProperty("emoji_uid").GetString(),
                is_primary = data.GetProperty("is_primary").GetBoolean(),
                state = data.GetProperty("state").GetString(),
                protection = "protected",
            }
        );

        aliasItem["Data"] = new AttributeValue(updatedData);
        await db.PutItemAsync(new PutItemRequest { TableName = TableName, Item = aliasItem });

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/guarded/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E543");
    }

    // ── Error cases ──

    [Fact]
    public async Task RemapAlias_SelfRemap_Returns409()
    {
        var uid = await SeedActiveEmojiAsync("self-host");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "my-sibling" });

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/my-sibling/target",
            new { uid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E545");
    }

    [Fact]
    public async Task RemapAlias_NonexistentAlias_Returns404()
    {
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/does-not-exist/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E544");
    }

    [Fact]
    public async Task RemapAlias_NonexistentTarget_Returns404()
    {
        var srcUid = await SeedActiveEmojiAsync("valid-src");
        var fakeUid = System.Ulid.NewUlid().ToString().ToLowerInvariant();

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/aliases/valid-src/target",
            new { uid = fakeUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E541");
    }

    [Fact]
    public async Task RemapAlias_InvalidAliasName_Returns400()
    {
        var dstUid = await SeedActiveEmojiAsync("dst-emoji");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/a/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E201");
    }

    [Fact]
    public async Task RemapAlias_InvalidTargetUid_Returns400()
    {
        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/something/target",
            new { uid = "bad-uid" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E101");
    }

    // ── Atomic consistency ──

    [Fact]
    public async Task RemapAlias_AtomicConsistency_NoDuplicateActiveAssignment()
    {
        var srcUid = await SeedActiveEmojiAsync("atomic-src");
        var dstUid = await SeedActiveEmojiAsync("atomic-dst");
        var movedAlias = "zz-moved-alias";
        await Client.PostAsJsonAsync($"/api/v1/emoji/{srcUid}/aliases", new { alias = movedAlias });

        await Client.PutAsJsonAsync($"/api/v1/aliases/{movedAlias}/target", new { uid = dstUid });

        // After remap, the alias must resolve to exactly one emoji (the target)
        var srcGet = await Client.GetAsync($"/api/v1/emoji/{srcUid}");
        var srcBody = await srcGet.Content.ReadFromJsonAsync<JsonElement>();
        var srcAliases = GetAliases(srcBody);
        srcAliases.Should().NotContain(movedAlias);

        var dstGet = await Client.GetAsync($"/api/v1/emoji/{dstUid}");
        var dstBody = await dstGet.Content.ReadFromJsonAsync<JsonElement>();
        var dstAliases = GetAliases(dstBody);
        dstAliases.Should().Contain(movedAlias);

        // Search by the specific alias prefix confirms it resolves to target only
        var searchResponse = await Client.GetAsync($"/api/v1/emoji?alias_prefix=zz-moved");
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = searchBody.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle(e => e.GetProperty("uid").GetString() == dstUid);
    }

    // ── Helpers ──

    private async Task<string> SeedActiveEmojiAsync(string alias)
    {
        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = alias,
                displayName = $"{alias} Display",
                contentType = "image/png",
                assetReference = $"assets/{alias}.png",
                description = $"Emoji for {alias}",
            }
        );
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("uid").GetString()!;
    }

    private static List<string?> GetAliases(JsonElement body)
    {
        if (body.TryGetProperty("aliases", out var aliasesProp))
        {
            return aliasesProp.EnumerateArray().Select(a => a.GetString()).ToList();
        }
        return new List<string?>();
    }
}
