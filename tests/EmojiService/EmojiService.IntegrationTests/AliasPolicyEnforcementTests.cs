using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class AliasPolicyEnforcementTests : DynamoDbIntegrationTest
{
    public AliasPolicyEnforcementTests()
        : base("policy") { }

    // ── Reserved alias enforcement ──

    [Fact]
    public async Task CreateEmoji_ReservedAlias_Returns409()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "shipit",
                displayName = "Ship It",
                contentType = "image/png",
                assetReference = "assets/shipit.png",
                description = "Should be blocked",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E600");
        error.GetProperty("detail").GetString().Should().Contain("shipit");
    }

    [Fact]
    public async Task AddAlias_ReservedAlias_Returns409()
    {
        var uid = await SeedActiveEmojiAsync("normal-emoji");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "approved" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E600");
    }

    [Fact]
    public async Task CreateEmoji_ReservedPrefix_Returns409()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "acme-custom-emoji",
                displayName = "Acme Custom",
                contentType = "image/png",
                assetReference = "assets/acme.png",
                description = "Should match reserved prefix",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E600");
        error.GetProperty("detail").GetString().Should().Contain("acme");
    }

    // ── Blocked alias enforcement ──

    [Fact]
    public async Task CreateEmoji_BlockedAlias_Returns403()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "admin",
                displayName = "Admin Emoji",
                contentType = "image/png",
                assetReference = "assets/admin.png",
                description = "Should be blocked",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E601");
        error.GetProperty("detail").GetString().Should().Contain("admin");
    }

    [Fact]
    public async Task AddAlias_BlockedAlias_Returns403()
    {
        var uid = await SeedActiveEmojiAsync("normal-emoji");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "root" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E601");
    }

    [Fact]
    public async Task RemapAlias_BlockedAlias_Returns403()
    {
        var srcUid = await SeedActiveEmojiAsync("blocked-src");
        var dstUid = await SeedActiveEmojiAsync("blocked-dst");

        // Directly create a blocked-named alias item in DynamoDB
        await PutAliasItemAsync("admin", srcUid);

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/admin/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E601");
    }

    [Fact]
    public async Task RemapAlias_BlockedAlias_OverrideDoesNotBypass()
    {
        var srcUid = await SeedActiveEmojiAsync("blocked-src2");
        var dstUid = await SeedActiveEmojiAsync("blocked-dst2");

        await PutAliasItemAsync("root", srcUid);

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/root/target",
            new
            {
                uid = dstUid,
                overrideProtection = true,
                reason = "Testing blocked enforcement",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E601");
    }

    // ── Protected alias override ──

    [Fact]
    public async Task RemapAlias_ProtectedAlias_StandardPathReturns403()
    {
        var srcUid = await SeedActiveEmojiAsync("guarded");
        var dstUid = await SeedActiveEmojiAsync("safe-target");
        await MarkAliasAsProtected("guarded");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/guarded/target",
            new { uid = dstUid }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E543");
    }

    [Fact]
    public async Task RemapAlias_ProtectedAlias_OverrideSucceeds()
    {
        var srcUid = await SeedActiveEmojiAsync("override-me");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/aliases",
            new { alias = "override-fallback" }
        );
        var dstUid = await SeedActiveEmojiAsync("override-dst");
        await MarkAliasAsProtected("override-me");

        var response = await Client.PutAsJsonAsync(
            "/api/v1/aliases/override-me/target",
            new
            {
                uid = dstUid,
                overrideProtection = true,
                reason = "Testing protected override",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("alias").GetString().Should().Be("override-me");
        body.GetProperty("previousEmojiUid").GetString().Should().Be(srcUid);
        body.GetProperty("targetEmojiUid").GetString().Should().Be(dstUid);
        body.GetProperty("overrideUsed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RemapAlias_ProtectedAlias_OverrideWritesAuditEvent()
    {
        var srcUid = await SeedActiveEmojiAsync("audit-guard");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/aliases",
            new { alias = "audit-fallback" }
        );
        var dstUid = await SeedActiveEmojiAsync("audit-ovr-dst");
        await MarkAliasAsProtected("audit-guard");

        await Client.PutAsJsonAsync(
            "/api/v1/aliases/audit-guard/target",
            new
            {
                uid = dstUid,
                overrideProtection = true,
                reason = "Emergency reassignment",
            }
        );

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{srcUid}/audit");
        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        var remapEvent = items.First(i =>
            i.GetProperty("action").GetString() == "alias.remap_override"
        );
        remapEvent.GetProperty("reason").GetString().Should().Contain("Emergency reassignment");
        remapEvent.GetProperty("subjectUid").GetString().Should().Be(srcUid);
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

    private async Task PutAliasItemAsync(string aliasName, string emojiUid)
    {
        var db = Factory.Services.GetRequiredService<IAmazonDynamoDB>();
        await db.PutItemAsync(
            new PutItemRequest
            {
                TableName = TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue($"ALIAS#{aliasName}"),
                    ["SK"] = new AttributeValue("META"),
                    ["EntityType"] = new AttributeValue("Alias"),
                    ["GSI1PK"] = new AttributeValue($"EMOJI#{emojiUid}"),
                    ["GSI1SK"] = new AttributeValue($"ALIAS#{aliasName}"),
                    ["Data"] = new AttributeValue(
                        JsonSerializer.Serialize(
                            new
                            {
                                name = aliasName,
                                emoji_uid = emojiUid,
                                is_primary = true,
                                state = "active",
                                protection = "normal",
                            }
                        )
                    ),
                },
            }
        );
    }

    private async Task MarkAliasAsProtected(string aliasName)
    {
        var db = Factory.Services.GetRequiredService<IAmazonDynamoDB>();
        var getResponse = await db.GetItemAsync(
            new GetItemRequest
            {
                TableName = TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue($"ALIAS#{aliasName}"),
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
    }
}
