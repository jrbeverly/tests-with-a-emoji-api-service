using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmojiService.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.IntegrationTests;

public class EmojiLifecycleTests : DynamoDbIntegrationTest
{
    public EmojiLifecycleTests()
        : base("lifecycle") { }

    // ── Legal transitions ──

    [Fact]
    public async Task Deprecate_ActiveToDeprecated_Returns200()
    {
        var uid = await SeedActiveEmojiAsync("deprecate-me");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "deprecated", reason = "No longer relevant" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("deprecated");
    }

    [Fact]
    public async Task Promote_DeprecatedToActive_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("promote-me", LifecycleState.Deprecated);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "active", reason = "Still useful" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("active");
    }

    [Fact]
    public async Task Disable_ActiveToDisabled_Returns200()
    {
        var uid = await SeedActiveEmojiAsync("disable-me");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "disabled" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task Disable_DeprecatedToDisabled_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("disable-dep", LifecycleState.Deprecated);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "disabled" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task Enable_DisabledToActive_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("enable-me", LifecycleState.Disabled);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "active", reason = "Re-enabled" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("active");
    }

    [Fact]
    public async Task Remove_DisabledToRemoved_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("remove-me", LifecycleState.Disabled);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "removed" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("removed");
    }

    [Fact]
    public async Task Approve_PendingToActive_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("approve-me", LifecycleState.Pending);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "active" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("active");
    }

    [Fact]
    public async Task Reject_PendingToRemoved_Returns200()
    {
        var uid = await SeedEmojiInStateAsync("reject-me", LifecycleState.Pending);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "removed", reason = "Does not meet guidelines" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lifecycleState").GetString().Should().Be("removed");
    }

    // ── Illegal transitions (409) ──

    [Fact]
    public async Task IllegalTransition_ActiveToPending_Returns409()
    {
        var uid = await SeedActiveEmojiAsync("no-pending");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "pending" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Invalid state transition");
        body.GetProperty("status").GetInt32().Should().Be(409);
        body.GetProperty("detail").GetString().Should().Contain("deprecated");
        body.GetProperty("detail").GetString().Should().Contain("disabled");
    }

    [Fact]
    public async Task IllegalTransition_DeprecatedToRemoved_Returns409()
    {
        var uid = await SeedEmojiInStateAsync("no-remove", LifecycleState.Deprecated);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "removed" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Invalid state transition");
        body.GetProperty("detail").GetString().Should().Contain("active");
        body.GetProperty("detail").GetString().Should().Contain("disabled");
    }

    [Fact]
    public async Task IllegalTransition_RemovedToAnything_Returns409()
    {
        var uid = await SeedEmojiInStateAsync("terminal", LifecycleState.Removed);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "active" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task IllegalTransition_SameState_Returns409()
    {
        var uid = await SeedActiveEmojiAsync("same-state");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "active" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Audit events ──

    [Fact]
    public async Task Deprecate_WritesAuditEvent()
    {
        var uid = await SeedActiveEmojiAsync("audited-deprecate");

        await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "deprecated", reason = "For audit test" }
        );

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        // First event (newest) should be the deprecation
        items[0].GetProperty("action").GetString().Should().Be("emoji.deprecate");
        items[0].GetProperty("actor").GetString().Should().Be("system");
        items[0].GetProperty("reason").GetString().Should().Be("For audit test");
        items[0].GetProperty("before").ValueKind.Should().Be(JsonValueKind.String);
        items[0].GetProperty("after").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Disable_WritesAuditEvent()
    {
        var uid = await SeedActiveEmojiAsync("audited-disable");

        await Client.PutAsJsonAsync($"/api/v1/emoji/{uid}/state", new { state = "disabled" });

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("action").GetString().Should().Be("emoji.disable");
    }

    [Fact]
    public async Task Approve_WritesAuditEvent()
    {
        var uid = await SeedEmojiInStateAsync("audited-approve", LifecycleState.Pending);

        await Client.PutAsJsonAsync($"/api/v1/emoji/{uid}/state", new { state = "active" });

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("action").GetString().Should().Be("emoji.approve");
    }

    // ── Error cases ──

    [Fact]
    public async Task InvalidUid_Returns400()
    {
        var response = await Client.PutAsJsonAsync(
            "/api/v1/emoji/bad-uid/state",
            new { state = "active" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownState_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("bad-state");

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{uid}/state",
            new { state = "nonexistent" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("E400");
    }

    [Fact]
    public async Task NonExistentEmoji_Returns404()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/v1/emoji/{Ulid.NewUlid().ToString().ToLowerInvariant()}/state",
            new { state = "active" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private async Task<string> SeedEmojiInStateAsync(string alias, LifecycleState state)
    {
        var repo = Factory.Services.GetRequiredService<IEmojiRepository>();
        var index = Factory.Services.GetRequiredService<IEmojiIndex>();

        var uid = EmojiUid.Create(System.Ulid.NewUlid().ToString().ToLowerInvariant()).Value!;
        var aliasResult = Alias.Create(alias).Value!;
        var now = DateTime.UtcNow;

        var emojiResult = DomainEmoji.Create(
            uid,
            aliasResult,
            $"{alias} Display",
            $"Emoji for {alias}",
            "image/png",
            $"assets/{alias}.png",
            now,
            now,
            state: state
        );

        emojiResult.IsSuccess.Should().BeTrue();
        var emoji = emojiResult.Value!;

        await repo.SaveAsync(emoji);
        index.Upsert(emoji);

        return uid.Value;
    }
}
