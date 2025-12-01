using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EmojiService.Tests;

public class EmojiMetadataRoundTripTests
    : IClassFixture<WebApplicationFactory<Program>>,
        IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IAmazonDynamoDB _dynamoDb;
    private const string TableName = "emoji-registry";

    public EmojiMetadataRoundTripTests(WebApplicationFactory<Program> factory)
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

            // Wait for table to become active
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
            // Table already exists — delete and recreate
            await _dynamoDb.DeleteTableAsync(TableName);
            await InitializeAsync();
        }
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

    [Fact]
    public async Task UploadThenResolve_RoundTripsMetadata()
    {
        var client = _factory.CreateClient();

        var payload = new
        {
            primaryAlias = "party-parrot",
            displayName = "Party Parrot",
            description = "A festive parrot emoji",
            contentType = "image/gif",
            assetReference = "assets/party.gif",
            tags = new[] { "party", "festive", "fun" },
            categories = new[] { "celebration", "animals" },
            owner = "team-fun",
        };

        var createResponse = await client.PostAsJsonAsync("/api/v1/emoji", payload);
        if (!createResponse.IsSuccessStatusCode)
        {
            var errorBody = await createResponse.Content.ReadAsStringAsync();
            createResponse
                .StatusCode.Should()
                .Be(HttpStatusCode.Created, $"API returned error: {errorBody}");
        }
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        created.GetProperty("displayName").GetString().Should().Be("Party Parrot");
        created.GetProperty("description").GetString().Should().Be("A festive parrot emoji");
        created.GetProperty("primaryAlias").GetString().Should().Be("party-parrot");
        created.GetProperty("contentType").GetString().Should().Be("image/gif");
        created.GetProperty("assetReference").GetString().Should().Be("assets/party.gif");
        created.GetProperty("owner").GetString().Should().Be("team-fun");

        created
            .GetProperty("tags")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo(["party", "festive", "fun"]);
        created
            .GetProperty("categories")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo(["celebration", "animals"]);

        // Resolve
        var getResponse = await client.GetAsync($"/api/v1/emoji/{uid}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resolved = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

        resolved.GetProperty("uid").GetString().Should().Be(uid);
        resolved.GetProperty("displayName").GetString().Should().Be("Party Parrot");
        resolved.GetProperty("description").GetString().Should().Be("A festive parrot emoji");
        resolved.GetProperty("primaryAlias").GetString().Should().Be("party-parrot");
        resolved.GetProperty("contentType").GetString().Should().Be("image/gif");
        resolved.GetProperty("assetReference").GetString().Should().Be("assets/party.gif");
        resolved.GetProperty("owner").GetString().Should().Be("team-fun");
        resolved
            .GetProperty("tags")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo(["party", "festive", "fun"]);
        resolved
            .GetProperty("categories")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo(["celebration", "animals"]);
    }

    [Fact]
    public async Task GetEmoji_NotFound_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/emoji/01jabc123xyz4567890abcdefg");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEmoji_InvalidUid_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/emoji/not-a-valid-uid");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
