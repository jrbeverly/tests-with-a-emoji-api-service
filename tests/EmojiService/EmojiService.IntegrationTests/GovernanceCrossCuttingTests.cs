using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class GovernanceCrossCuttingTests : DynamoDbIntegrationTest
{
    public GovernanceCrossCuttingTests()
        : base("gov-crossover") { }

    [Fact]
    public async Task FullGovernanceWorkflow_AuditTrailContainsAllEventsInOrder()
    {
        // Phase 1 — Create the source emoji
        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "gov-source",
                displayName = "Governance Source",
                contentType = "image/png",
                assetReference = "assets/gov-source.png",
                description = "Source emoji for governance cross-cutting test",
            }
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var srcUid = created.GetProperty("uid").GetString()!;

        // Phase 2 — Add a secondary alias to the source
        var addAliasResponse = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/aliases",
            new { alias = "gov-migrant" }
        );
        addAliasResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Phase 3 — Create a target emoji (recipient for remap)
        var createTarget = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "gov-target",
                displayName = "Governance Target",
                contentType = "image/png",
                assetReference = "assets/gov-target.png",
                description = "Target emoji for governance cross-cutting test",
            }
        );
        createTarget.StatusCode.Should().Be(HttpStatusCode.Created);
        var targetCreated = await createTarget.Content.ReadFromJsonAsync<JsonElement>();
        var dstUid = targetCreated.GetProperty("uid").GetString()!;

        // Phase 4 — Remap the alias from source to target
        var remapResponse = await Client.PutAsJsonAsync(
            "/api/v1/aliases/gov-migrant/target",
            new { uid = dstUid, reason = "Governance cross-cutting remap" }
        );

        remapResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Phase 5 — Deprecate the source emoji
        var deprecateResponse = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{srcUid}/state",
            new { state = "deprecated", reason = "Superseded by governance test" }
        );
        deprecateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // ── Audit trail: source emoji (newest-first) ──

        var srcAudit = await Client.GetAsync($"/api/v1/emoji/{srcUid}/audit");
        srcAudit.StatusCode.Should().Be(HttpStatusCode.OK);
        var srcBody = await srcAudit.Content.ReadFromJsonAsync<JsonElement>();
        var srcItems = srcBody.GetProperty("items").EnumerateArray().ToList();

        srcItems.Should().HaveCount(4);

        srcItems[0].GetProperty("action").GetString().Should().Be("emoji.deprecate");
        srcItems[0].GetProperty("reason").GetString().Should().Be("Superseded by governance test");

        srcItems[1].GetProperty("action").GetString().Should().Be("alias.remap");
        srcItems[1]
            .GetProperty("reason")
            .GetString()
            .Should()
            .Contain("Governance cross-cutting remap");

        srcItems[2].GetProperty("action").GetString().Should().Be("alias.add");

        srcItems[3].GetProperty("action").GetString().Should().Be("emoji.upload");

        foreach (var item in srcItems)
        {
            item.GetProperty("subjectUid").GetString().Should().Be(srcUid);
            item.GetProperty("eventId").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("occurredAt").GetString().Should().NotBeNullOrEmpty();
        }

        // ── Audit trail: target emoji received the remap event ──

        var dstAudit = await Client.GetAsync($"/api/v1/emoji/{dstUid}/audit");
        dstAudit.StatusCode.Should().Be(HttpStatusCode.OK);
        var dstBody = await dstAudit.Content.ReadFromJsonAsync<JsonElement>();
        var dstItems = dstBody.GetProperty("items").EnumerateArray().ToList();

        var dstRemapEvent = dstItems
            .Should()
            .ContainSingle(i => i.GetProperty("action").GetString() == "alias.remap")
            .Which;
        dstRemapEvent.GetProperty("subjectUid").GetString().Should().Be(dstUid);

        // ── Data integrity: alias moved to target exclusively ──

        var srcGet = await Client.GetAsync($"/api/v1/emoji/{srcUid}");
        var srcEmoji = await srcGet.Content.ReadFromJsonAsync<JsonElement>();
        var srcAliases = GetAliases(srcEmoji);
        srcAliases.Should().NotContain("gov-migrant");
        srcEmoji.GetProperty("lifecycleState").GetString().Should().Be("deprecated");

        var dstGet = await Client.GetAsync($"/api/v1/emoji/{dstUid}");
        var dstEmoji = await dstGet.Content.ReadFromJsonAsync<JsonElement>();
        var dstAliases = GetAliases(dstEmoji);
        dstAliases.Should().Contain("gov-migrant");

        // ── Global audit: includes events from both emoji ──

        var globalAudit = await Client.GetAsync("/api/v1/audit");
        globalAudit.StatusCode.Should().Be(HttpStatusCode.OK);
        var globalBody = await globalAudit.Content.ReadFromJsonAsync<JsonElement>();
        var globalItems = globalBody.GetProperty("items").EnumerateArray().ToList();

        var globalActions = globalItems.Select(i => i.GetProperty("action").GetString()).ToList();
        globalActions.Should().Contain("emoji.deprecate");
        globalActions.Should().Contain("alias.remap");
        globalActions.Should().Contain("alias.add");
        globalActions.Should().Contain("emoji.upload");
    }

    [Fact]
    public async Task ReservedAlias_BlocksCreation_AndUnreservedSucceeds()
    {
        var blocked = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "shipit",
                displayName = "Reserved",
                contentType = "image/png",
                assetReference = "assets/shipit.png",
            }
        );
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockedError = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        blockedError.GetProperty("title").GetString().Should().Be("E600");

        var allowed = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "gov-unreserved",
                displayName = "Unreserved",
                contentType = "image/png",
                assetReference = "assets/ok.png",
            }
        );
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task BlockedAlias_RejectedOnCreate_AndRemap()
    {
        var create = await Client.PostAsJsonAsync(
            "/api/v1/emoji",
            new
            {
                primaryAlias = "admin",
                displayName = "Blocked",
                contentType = "image/png",
                assetReference = "assets/admin.png",
            }
        );
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var uid = await SeedActiveEmojiAsync("gov-blocked");
        var add = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "root" }
        );
        add.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LifecycleTransition_CannotJumpFromActiveToRemoved()
    {
        var uid = await SeedActiveEmojiAsync("gov-active");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "removed" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("status").GetInt32().Should().Be(409);
    }

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
