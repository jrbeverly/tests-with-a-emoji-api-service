using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DomainEmoji = EmojiService.Domain.Emoji;

namespace EmojiService.Tests;

public class SearchEmojiRouteTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IAmazonDynamoDB _dynamoDb;
    private const string TableName = "emoji-registry-search";

    private static readonly List<object> SeedPayloads = new()
    {
        new
        {
            primaryAlias = "party-parrot",
            displayName = "Party Parrot",
            contentType = "image/gif",
            assetReference = "assets/party.gif",
            description = "A festive parrot",
            tags = new[] { "party", "fun", "reaction" },
            categories = new[] { "fun", "social" },
            owner = "team-a",
        },
        new
        {
            primaryAlias = "verified",
            displayName = "Verified Stamp",
            contentType = "image/png",
            assetReference = "assets/verified.png",
            description = "Verification stamp emoji",
            tags = new[] { "reaction", "workflow" },
            categories = new[] { "approvals" },
            owner = "eng-team",
        },
        new
        {
            primaryAlias = "thinking-cat",
            displayName = "Thinking Cat",
            contentType = "image/png",
            assetReference = "assets/think.png",
            description = "A thoughtful cat",
            tags = new[] { "reaction", "funny" },
            categories = new[] { "fun", "social" },
            owner = "team-a",
        },
        new
        {
            primaryAlias = "thumbs-up",
            displayName = "Thumbs Up",
            contentType = "image/png",
            assetReference = "assets/thumbs.png",
            description = "Thumbs up reaction",
            tags = new[] { "reaction", "approval" },
            categories = new[] { "work" },
            owner = "team-b",
        },
        new
        {
            primaryAlias = "old-smile",
            displayName = "Old Smile",
            contentType = "image/png",
            assetReference = "assets/old.png",
            description = "Legacy smile emoji",
            tags = new[] { "legacy" },
            categories = new[] { "fun" },
            owner = "team-a",
        },
        new
        {
            primaryAlias = "celebrate",
            displayName = "Celebrate",
            contentType = "image/gif",
            assetReference = "assets/party2.gif",
            description = "Celebration emoji",
            tags = new[] { "party", "fun" },
            categories = new[] { "fun", "events" },
            owner = "eng-team",
        },
        new
        {
            primaryAlias = "verified-v2",
            displayName = "Verified v2",
            contentType = "image/png",
            assetReference = "assets/verified2.png",
            description = "Updated verification stamp",
            tags = new[] { "reaction", "workflow", "new" },
            categories = new[] { "approvals", "work" },
            owner = "eng-team",
        },
    };

    public SearchEmojiRouteTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DynamoDB:TableName", TableName);
            builder.UseSetting("DynamoDB:ServiceUrl", "http://localhost:8000");
            builder.UseSetting("DynamoDB:Region", "us-east-1");
        });

        var config = new AmazonDynamoDBConfig { ServiceURL = "http://localhost:8000" };
        _dynamoDb = new AmazonDynamoDBClient("fake", "fake", config);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _dynamoDb.CreateTableAsync(
                new CreateTableRequest
                {
                    TableName = TableName,
                    KeySchema =
                    [
                        new KeySchemaElement("PK", KeyType.HASH),
                        new KeySchemaElement("SK", KeyType.RANGE),
                    ],
                    AttributeDefinitions =
                    [
                        new AttributeDefinition("PK", ScalarAttributeType.S),
                        new AttributeDefinition("SK", ScalarAttributeType.S),
                    ],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }
            );

            var active = false;
            for (var i = 0; i < 10; i++)
            {
                var desc = await _dynamoDb.DescribeTableAsync(TableName);
                if (desc.Table.TableStatus == TableStatus.ACTIVE)
                {
                    active = true;
                    break;
                }
                await Task.Delay(200);
            }

            if (!active)
                throw new InvalidOperationException("DynamoDB table did not become active in time");
        }
        catch (ResourceInUseException)
        {
            await _dynamoDb.DeleteTableAsync(TableName);
            await InitializeAsync();
            return;
        }

        // Seed via the API so the index picks up the data
        var client = _factory.CreateClient();
        foreach (var payload in SeedPayloads)
        {
            var response = await client.PostAsJsonAsync("/api/v1/emoji", payload);
            response.EnsureSuccessStatusCode();
        }

        // Mark old-smile as deprecated: update index + DynamoDB via service provider
        var repo = _factory.Services.GetRequiredService<IEmojiRepository>();
        var index = _factory.Services.GetRequiredService<IEmojiIndex>();
        var allEmoji = await repo.ScanAllAsync();
        var oldSmile = allEmoji.First(e => e.PrimaryAlias.Value == "old-smile");
        var deprecated = oldSmile with { LifecycleState = LifecycleState.Deprecated };
        index.Upsert(deprecated);
        await repo.SaveAsync(deprecated);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _dynamoDb.DeleteTableAsync(TableName);
        }
        catch { }
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    // ── Tag filter ──

    [Fact]
    public async Task FilterBySingleTag_ReturnsMatchingEmoji()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?tag=party");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["party-parrot", "celebrate"]);
    }

    [Fact]
    public async Task FilterByMultipleTags_AndsResults()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?tag=reaction,workflow");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["verified", "verified-v2"]);
    }

    [Fact]
    public async Task FilterByTag_NoMatch_ReturnsEmpty()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?tag=nonexistent");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    // ── Category filter ──

    [Fact]
    public async Task FilterByCategory_ReturnsMatchingEmoji()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?category=approvals");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["verified", "verified-v2"]);
    }

    // ── Owner filter ──

    [Fact]
    public async Task FilterByOwner_ReturnsMatchingEmoji()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?owner=eng-team");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["verified", "celebrate", "verified-v2"]);
    }

    // ── State filter ──

    [Fact]
    public async Task DefaultState_ReturnsOnlyActive()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().OnlyContain(e => e.GetProperty("state").GetString() == "active");
        items.Should().HaveCount(6);
    }

    [Fact]
    public async Task FilterByStateDeprecated_ReturnsOnlyDeprecated()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?state=deprecated");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["old-smile"]);
    }

    // ── Alias prefix filter ──

    [Fact]
    public async Task FilterByAliasPrefix_ReturnsMatchingEmoji()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?alias_prefix=ver");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["verified", "verified-v2"]);
    }

    // ── Combined filters ──

    [Fact]
    public async Task CombinedFilters_AndsAllConditions()
    {
        var client = CreateClient();

        var response = await client.GetAsync(
            "/api/v1/emoji?tag=reaction&category=work&owner=eng-team"
        );
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var aliases = GetAliases(body);

        aliases.Should().BeEquivalentTo(["verified-v2"]);
    }

    [Fact]
    public async Task CombinedFilters_NoMatch_ReturnsEmpty()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?tag=party&owner=team-b");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    // ── Pagination ──

    [Fact]
    public async Task Pagination_ReturnsStablePages()
    {
        var client = CreateClient();

        var response1 = await client.GetAsync("/api/v1/emoji?limit=3");
        response1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var page1 = GetUids(body1);

        page1.Should().HaveCount(3);
        body1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor = body1.GetProperty("cursor").GetString();
        cursor.Should().NotBeNull();

        var response2 = await client.GetAsync($"/api/v1/emoji?limit=3&cursor={cursor}");
        response2.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var page2 = GetUids(body2);

        page2.Should().HaveCount(3);
        body2.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        var allUids = page1.Concat(page2).ToList();
        allUids.Should().OnlyHaveUniqueItems();
        allUids.Should().HaveCount(6);

        // Verify newest-first ordering (ULIDs are time-sortable)
        var sorted = allUids.OrderDescending().ToList();
        allUids.Should().Equal(sorted);
    }

    [Fact]
    public async Task Pagination_SameFilters_ReturnsIdenticalFirstPage()
    {
        var client = CreateClient();

        var response1 = await client.GetAsync("/api/v1/emoji?tag=reaction&limit=2");
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var page1 = GetUids(body1);

        var response2 = await client.GetAsync("/api/v1/emoji?tag=reaction&limit=2");
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var page2 = GetUids(body2);

        page1.Should().Equal(page2);
    }

    // ── Validation ──

    [Fact]
    public async Task InvalidLimit_Returns400()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?limit=0");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvalidState_Returns400()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/v1/emoji?state=invalid");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // ── Helpers ──

    private static List<string?> GetAliases(JsonElement body)
    {
        return body.GetProperty("items")
            .EnumerateArray()
            .Select(e => e.GetProperty("primaryAlias").GetString())
            .ToList();
    }

    private static List<string?> GetUids(JsonElement body)
    {
        return body.GetProperty("items")
            .EnumerateArray()
            .Select(e => e.GetProperty("uid").GetString())
            .ToList();
    }
}
