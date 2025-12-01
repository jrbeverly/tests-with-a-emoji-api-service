using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EmojiService.IntegrationTests;

public class BatchAndSearchAcceptanceTests
    : IClassFixture<WebApplicationFactory<Program>>,
        IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IAmazonDynamoDB _dynamoDb;
    private const string TableName = "emoji-registry-batchsearch";

    public BatchAndSearchAcceptanceTests(WebApplicationFactory<Program> factory)
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
                        new AttributeDefinition("GSI1PK", ScalarAttributeType.S),
                        new AttributeDefinition("GSI1SK", ScalarAttributeType.S),
                    ],
                    GlobalSecondaryIndexes =
                    [
                        new GlobalSecondaryIndex
                        {
                            IndexName = "GSI1",
                            KeySchema =
                            [
                                new KeySchemaElement("GSI1PK", KeyType.HASH),
                                new KeySchemaElement("GSI1SK", KeyType.RANGE),
                            ],
                            Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        },
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
                throw new InvalidOperationException(
                    $"DynamoDB table '{TableName}' did not become active in time"
                );
        }
        catch (ResourceInUseException)
        {
            await _dynamoDb.DeleteTableAsync(TableName);
            await Task.Delay(500);
            await InitializeAsync();
            return;
        }

        // Seed the curated dataset via batch apply
        using var client = _factory.CreateClient();
        using var content = CuratedDataset.CreateBatchContent();
        var response = await client.PostAsync("/api/v1/batch/apply", content);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _dynamoDb.DeleteTableAsync(TableName);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    // ── Batch upload acceptance ──

    [Fact]
    public async Task BatchUpload_AllItemsSucceed()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?limit=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(CuratedDataset.ItemCount);
    }

    [Fact]
    public async Task BatchUpload_AllItemsIndividuallyResolvable()
    {
        using var client = CreateClient();
        var searchResponse = await client.GetAsync("/api/v1/emoji?limit=100");
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = searchBody.GetProperty("items").EnumerateArray().ToList();

        foreach (var item in items)
        {
            var uid = item.GetProperty("uid").GetString()!;
            var getResponse = await client.GetAsync($"/api/v1/emoji/{uid}");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    // ── Tag filter (single) ──

    [Fact]
    public async Task Search_ByTag_Dev_ReturnsDevEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=dev&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo([
                "code-review",
                "rocket-ship",
                "bug-report",
                "deploy",
                "coffee-mug",
                "lock-icon",
            ]);
    }

    [Fact]
    public async Task Search_ByTag_Reaction_ReturnsReactionEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=reaction&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .Contain([
                "party-parrot",
                "thinking-cat",
                "thumbs-up",
                "wave-hello",
                "fire-reaction",
                "heart-reaction",
                "cross-mark",
                "hundred-points",
            ]);
    }

    [Fact]
    public async Task Search_ByTag_Approval_ReturnsApprovalEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=approval&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["thumbs-up", "check-mark", "star-icon-reaction"]);
    }

    // ── Tag filter (multiple, AND) ──

    [Fact]
    public async Task Search_ByTags_DevAndOps_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=dev,ops&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["bug-report", "deploy"]);
    }

    [Fact]
    public async Task Search_ByTags_ReactionAndFunny_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=reaction,funny&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["thinking-cat"]);
    }

    [Fact]
    public async Task Search_ByTags_WorkflowAndDev_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=workflow,dev&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["code-review", "deploy", "lock-icon"]);
    }

    // ── Category filter ──

    [Fact]
    public async Task Search_ByCategory_DevTools_ReturnsDevToolsEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?category=dev-tools&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo([
                "code-review",
                "rocket-ship",
                "bug-report",
                "deploy",
                "coffee-mug",
                "lock-icon",
            ]);
    }

    [Fact]
    public async Task Search_ByCategory_Events_ReturnsEventsEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?category=events&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["celebrate", "tada"]);
    }

    [Fact]
    public async Task Search_ByCategory_Approvals_ReturnsApprovalsEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?category=approvals&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["check-mark", "star-icon-reaction"]);
    }

    // ── Owner filter ──

    [Fact]
    public async Task Search_ByOwner_TeamAlpha_ReturnsTeamAlphaEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?owner=team-alpha&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo([
                "party-parrot",
                "celebrate",
                "thinking-cat",
                "fire-reaction",
                "coffee-mug",
                "globe-icon",
            ]);
    }

    [Fact]
    public async Task Search_ByOwner_EngTeam_ReturnsEngTeamEmoji()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?owner=eng-team&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo([
                "code-review",
                "rocket-ship",
                "bug-report",
                "deploy",
                "check-mark",
                "cross-mark",
                "lock-icon",
                "megaphone-icon",
            ]);
    }

    [Fact]
    public async Task Search_ByOwner_Nonexistent_ReturnsEmpty()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?owner=nonexistent-owner&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    // ── Alias prefix filter ──

    [Fact]
    public async Task Search_ByAliasPrefix_Rock_ReturnsRocketShip()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?alias_prefix=rock&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["rocket-ship"]);
    }

    [Fact]
    public async Task Search_ByAliasPrefix_De_ReturnsDeploy()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?alias_prefix=de&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["deploy"]);
    }

    [Fact]
    public async Task Search_ByAliasPrefix_Fire_ReturnsFireReaction()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?alias_prefix=fire&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["fire-reaction"]);
    }

    // ── State filter ──

    [Fact]
    public async Task Search_DefaultState_ReturnsOnlyActive()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?limit=100");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(CuratedDataset.ItemCount);
        items.Should().AllSatisfy(e => e.GetProperty("state").GetString().Should().Be("active"));
    }

    [Fact]
    public async Task Search_DeprecatedState_ReturnsNone()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?state=deprecated&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ── Combined filters ──

    [Fact]
    public async Task Search_Combined_TagAndCategory_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=dev&category=dev-tools&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo([
                "code-review",
                "rocket-ship",
                "bug-report",
                "deploy",
                "coffee-mug",
                "lock-icon",
            ]);
    }

    [Fact]
    public async Task Search_Combined_TagAndOwner_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync(
            "/api/v1/emoji?tag=reaction&owner=team-alpha&limit=50"
        );
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["party-parrot", "thinking-cat", "fire-reaction"]);
    }

    [Fact]
    public async Task Search_Combined_CategoryAndOwner_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?category=fun&owner=team-beta&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["tada"]);
    }

    [Fact]
    public async Task Search_Combined_TagCategoryAndOwner_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync(
            "/api/v1/emoji?tag=dev&category=dev-tools&owner=eng-team&limit=50"
        );
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .BeEquivalentTo(["code-review", "rocket-ship", "bug-report", "deploy", "lock-icon"]);
    }

    [Fact]
    public async Task Search_Combined_AliasPrefixAndTag_ReturnsIntersection()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?alias_prefix=co&tag=dev&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases.Should().BeEquivalentTo(["code-review", "coffee-mug"]);
    }

    [Fact]
    public async Task Search_Combined_NoMatch_ReturnsEmpty()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=party&owner=eng-team&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    // ── Pagination ──

    [Fact]
    public async Task Search_Pagination_ReturnsConsistentPages()
    {
        using var client = CreateClient();
        const int pageSize = 6;

        var response1 = await client.GetAsync($"/api/v1/emoji?limit={pageSize}");
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var page1Uids = GetUids(body1);

        page1Uids.Should().HaveCount(pageSize);
        body1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor1 = body1.GetProperty("cursor").GetString();
        cursor1.Should().NotBeNullOrEmpty();

        var response2 = await client.GetAsync($"/api/v1/emoji?limit={pageSize}&cursor={cursor1}");
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var page2Uids = GetUids(body2);

        page2Uids.Should().HaveCount(pageSize);
        body2.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor2 = body2.GetProperty("cursor").GetString();

        var response3 = await client.GetAsync($"/api/v1/emoji?limit={pageSize}&cursor={cursor2}");
        var body3 = await response3.Content.ReadFromJsonAsync<JsonElement>();
        var page3Uids = GetUids(body3);

        page3Uids.Should().HaveCount(pageSize);
        body3.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor3 = body3.GetProperty("cursor").GetString();

        var response4 = await client.GetAsync($"/api/v1/emoji?limit={pageSize}&cursor={cursor3}");
        var body4 = await response4.Content.ReadFromJsonAsync<JsonElement>();
        var page4Uids = GetUids(body4);

        page4Uids.Should().HaveCount(2);
        body4.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        var allUids = page1Uids.Concat(page2Uids).Concat(page3Uids).Concat(page4Uids).ToList();
        allUids.Should().HaveCount(CuratedDataset.ItemCount);
        allUids.Should().OnlyHaveUniqueItems();

        var sorted = allUids.OrderDescending().ToList();
        allUids
            .Should()
            .Equal(sorted, "items should be returned newest-first (ULID time-sortable)");
    }

    [Fact]
    public async Task Search_Pagination_SameRequestReturnsIdenticalPage()
    {
        using var client = CreateClient();
        var response1 = await client.GetAsync("/api/v1/emoji?limit=5");
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var page1 = GetUids(body1);

        var response2 = await client.GetAsync("/api/v1/emoji?limit=5");
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var page2 = GetUids(body2);

        page1.Should().Equal(page2);
    }

    [Fact]
    public async Task Search_Pagination_AllItemsCoveredInStream()
    {
        using var client = CreateClient();
        var uidsFound = new HashSet<string>();
        string? cursor = null;
        var hasMore = true;

        while (hasMore)
        {
            var url = $"/api/v1/emoji?limit=5{(cursor is null ? "" : $"&cursor={cursor}")}";
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            foreach (var uid in GetUids(body))
                uidsFound.Add(uid!);

            hasMore = body.GetProperty("hasMore").GetBoolean();
            cursor = body.GetProperty("cursor").GetString();
        }

        uidsFound.Should().HaveCount(CuratedDataset.ItemCount);
    }

    // ── Search response structure ──

    [Fact]
    public async Task Search_ResponseContainsAllRequiredFields()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?limit=1");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("cursor").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("hasMore")
            .ValueKind.Should()
            .BeOneOf(JsonValueKind.True, JsonValueKind.False);

        var item = body.GetProperty("items")[0];
        item.GetProperty("uid").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("primaryAlias").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("displayName").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("state").GetString().Should().Be("active");
        item.GetProperty("tags").ValueKind.Should().Be(JsonValueKind.Array);
        item.GetProperty("categories").ValueKind.Should().Be(JsonValueKind.Array);
        item.GetProperty("owner").ValueKind.Should().Be(JsonValueKind.String);
        item.GetProperty("createdAt").GetString().Should().NotBeNullOrEmpty();
    }

    // ── Filter format invariance ──

    [Fact]
    public async Task Search_TagFilterIsCaseInsensitive()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/v1/emoji?tag=DEV&limit=50");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var aliases = GetAliases(body);
        aliases
            .Should()
            .Contain([
                "code-review",
                "rocket-ship",
                "bug-report",
                "deploy",
                "coffee-mug",
                "lock-icon",
            ]);
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
