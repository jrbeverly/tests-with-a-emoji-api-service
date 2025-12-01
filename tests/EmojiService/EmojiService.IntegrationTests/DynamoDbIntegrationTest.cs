using System.Net.Http.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public abstract class DynamoDbIntegrationTest : IAsyncLifetime
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly WebApplicationFactory<Program> _factory;

    protected string TableName { get; }
    protected HttpClient Client { get; }
    protected WebApplicationFactory<Program> Factory => _factory;

    protected DynamoDbIntegrationTest(string tablePrefix)
    {
        TableName = $"emoji-it-{tablePrefix}";

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DynamoDB:TableName", TableName);
            builder.UseSetting("DynamoDB:ServiceUrl", "http://localhost:8000");
            builder.UseSetting("DynamoDB:Region", "us-east-1");
        });

        var config = new AmazonDynamoDBConfig { ServiceURL = "http://localhost:8000" };
        _dynamoDb = new AmazonDynamoDBClient("fake", "fake", config);

        Client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Table schema per ADR-0006: single-table with GSI1 for alias reverse lookup
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

        Client.Dispose();
        _factory.Dispose();
    }

    protected async Task SeedEmojiAsync(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        response.EnsureSuccessStatusCode();
    }
}
