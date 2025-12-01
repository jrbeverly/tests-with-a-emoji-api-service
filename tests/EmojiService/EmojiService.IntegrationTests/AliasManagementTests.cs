using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class AliasManagementTests : DynamoDbIntegrationTest
{
    public AliasManagementTests()
        : base("alias-mgmt") { }

    // ── Add alias ──

    [Fact]
    public async Task AddAlias_Returns201AndShowsInResponse()
    {
        var uid = await SeedActiveEmojiAsync("multi-alias");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "second-name" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("primaryAlias").GetString().Should().Be("multi-alias");

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["multi-alias", "second-name"]);
    }

    [Fact]
    public async Task AddAlias_ThenGetEmoji_ShowsAllAliases()
    {
        var uid = await SeedActiveEmojiAsync("get-all");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "alt-name" });

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["get-all", "alt-name"]);
    }

    [Fact]
    public async Task AddAlias_DuplicateActiveGlobally_Returns409()
    {
        var uid1 = await SeedActiveEmojiAsync("first-emoji");
        var uid2 = await SeedActiveEmojiAsync("second-emoji");

        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid1}/aliases",
            new { alias = "shared-alias" }
        );

        var conflict = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid2}/aliases",
            new { alias = "shared-alias" }
        );

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E513");
    }

    [Fact]
    public async Task AddAlias_SameAsPrimary_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("only-me");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "only-me" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E511");
    }

    [Fact]
    public async Task AddAlias_DuplicateOnSameEmoji_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("dup-test");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "my-alias" });

        var dup = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "my-alias" }
        );

        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await dup.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E512");
    }

    [Fact]
    public async Task AddAlias_InvalidAliasName_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("valid-emoji");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "a" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E201");
    }

    [Fact]
    public async Task AddAlias_NotFound_Returns404()
    {
        var fakeUid = System.Ulid.NewUlid().ToString().ToLowerInvariant();

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{fakeUid}/aliases",
            new { alias = "orphan" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E510");
    }

    [Fact]
    public async Task AddAlias_MultipleSecondaryAliases_AllListed()
    {
        var uid = await SeedActiveEmojiAsync("many-aliases");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "alias-1" });
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "alias-2" });
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "alias-3" });

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["many-aliases", "alias-1", "alias-2", "alias-3"]);
    }

    // ── Retire alias ──

    [Fact]
    public async Task RetireAlias_Returns204AndRemovesFromEmoji()
    {
        var uid = await SeedActiveEmojiAsync("retire-test");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "to-retire" });

        var response = await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/to-retire");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await Client.GetAsync($"/api/v1/emoji/{uid}");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["retire-test"]);
    }

    [Fact]
    public async Task RetireAlias_CannotRetirePrimary_Returns409()
    {
        var uid = await SeedActiveEmojiAsync("primary-only");

        var response = await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/primary-only");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E521");
    }

    [Fact]
    public async Task RetireAlias_NonExistentAlias_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("no-such");

        var response = await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E522");
    }

    [Fact]
    public async Task RetireAlias_NotFound_Returns404()
    {
        var fakeUid = System.Ulid.NewUlid().ToString().ToLowerInvariant();

        var response = await Client.DeleteAsync($"/api/v1/emoji/{fakeUid}/aliases/something");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E520");
    }

    [Fact]
    public async Task RetiredAliasName_CanBeReAddedBySameEmoji()
    {
        var uid = await SeedActiveEmojiAsync("retire-reuse");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "oneshot" });
        await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/oneshot");

        // Re-adding should succeed since the alias already exists (retired) for same emoji.
        // The retired alias item still exists, so AddAliasAsync will find it, but
        // existingEmojiUid == emoji's uid, so it bypasses the conflict check.
        // However, it's already been removed from SecondaryAliases, so the duplicate-on-same-emoji
        // check (E512) won't fire either. Let's verify it can be re-added.
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "oneshot" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── Set primary ──

    [Fact]
    public async Task SetPrimary_PromotesSecondaryToPrimary_Returns200()
    {
        var uid = await SeedActiveEmojiAsync("old-primary");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "new-primary" });

        var response = await Client.PutAsync(
            $"/api/v1/emoji/{uid}/aliases/new-primary/primary",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("primaryAlias").GetString().Should().Be("new-primary");

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["new-primary", "old-primary"]);
    }

    [Fact]
    public async Task SetPrimary_AlreadyPrimary_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("am-primary");

        var response = await Client.PutAsync(
            $"/api/v1/emoji/{uid}/aliases/am-primary/primary",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E531");
    }

    [Fact]
    public async Task SetPrimary_NonExistentAlias_Returns400()
    {
        var uid = await SeedActiveEmojiAsync("no-secondary");

        var response = await Client.PutAsync(
            $"/api/v1/emoji/{uid}/aliases/nonexistent/primary",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E532");
    }

    [Fact]
    public async Task SetPrimary_NotFound_Returns404()
    {
        var fakeUid = System.Ulid.NewUlid().ToString().ToLowerInvariant();

        var response = await Client.PutAsync(
            $"/api/v1/emoji/{fakeUid}/aliases/something/primary",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("title").GetString().Should().Be("E530");
    }

    // ── Resolve by alias ──

    [Fact]
    public async Task SearchByAliasPrefix_MatchesSecondaryAlias()
    {
        var uid = await SeedActiveEmojiAsync("primary-x");
        await Client.PostAsJsonAsync($"/api/v1/emoji/{uid}/aliases", new { alias = "secondary-y" });

        var response = await Client.GetAsync("/api/v1/emoji?alias_prefix=secondary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().ContainSingle(e => e.GetProperty("uid").GetString() == uid);
    }

    [Fact]
    public async Task SearchByAliasPrefix_DoesNotMatchRetiredAlias()
    {
        var uid = await SeedActiveEmojiAsync("visible-primary");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "gonna-retire" }
        );
        await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/gonna-retire");

        var response = await Client.GetAsync("/api/v1/emoji?alias_prefix=gonna");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().BeEmpty();
    }

    // ── Audit events ──

    [Fact]
    public async Task AddAlias_WritesAuditEvent()
    {
        var uid = await SeedActiveEmojiAsync("audited-add");

        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "audited-alias" }
        );

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("action").GetString().Should().Be("alias.add");
    }

    [Fact]
    public async Task RetireAlias_WritesAuditEvent()
    {
        var uid = await SeedActiveEmojiAsync("audited-retire");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "to-audit-retire" }
        );

        await Client.DeleteAsync($"/api/v1/emoji/{uid}/aliases/to-audit-retire");

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("action").GetString().Should().Be("alias.retire");
    }

    [Fact]
    public async Task SetPrimary_WritesAuditEvent()
    {
        var uid = await SeedActiveEmojiAsync("audited-primary");
        await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uid}/aliases",
            new { alias = "new-audited-primary" }
        );

        await Client.PutAsync($"/api/v1/emoji/{uid}/aliases/new-audited-primary/primary", null);

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("action").GetString().Should().Be("alias.set_primary");
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
