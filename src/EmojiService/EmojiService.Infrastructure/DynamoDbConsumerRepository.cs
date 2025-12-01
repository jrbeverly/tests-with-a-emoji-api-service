using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class DynamoDbConsumerRepository : IConsumerRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbConsumerRepository(IAmazonDynamoDB dynamoDb, DynamoDbOptions options)
    {
        _dynamoDb = dynamoDb;
        _tableName = options.TableName;
    }

    public async Task<Result> SaveAsync(Consumer consumer)
    {
        try
        {
            await _dynamoDb.PutItemAsync(
                new PutItemRequest { TableName = _tableName, Item = MapToItem(consumer) }
            );

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("E750", $"Failed to save consumer: {ex.Message}"));
        }
    }

    public async Task<Consumer?> GetByIdAsync(string id)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"CONSUMER#{id}"),
                ["SK"] = new AttributeValue("META"),
            },
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item.Count == 0 || !response.IsItemSet)
            return null;

        return MapFromItem(response.Item);
    }

    public async Task<IReadOnlyList<Consumer>> ScanAllAsync()
    {
        var consumers = new List<Consumer>();
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;

        do
        {
            var request = new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "EntityType = :type",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":type"] = new AttributeValue("Consumer"),
                },
                ExclusiveStartKey = exclusiveStartKey,
            };

            try
            {
                var response = await _dynamoDb.ScanAsync(request);

                foreach (var item in response.Items)
                {
                    var consumer = MapFromItem(item);
                    if (consumer is not null)
                        consumers.Add(consumer);
                }

                exclusiveStartKey =
                    response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
            }
            catch (ResourceNotFoundException)
            {
                break;
            }
        } while (exclusiveStartKey is not null);

        return consumers;
    }

    public async Task<Result> DeleteAsync(string id)
    {
        try
        {
            await _dynamoDb.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue($"CONSUMER#{id}"),
                        ["SK"] = new AttributeValue("META"),
                    },
                }
            );

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("E751", $"Failed to delete consumer: {ex.Message}"));
        }
    }

    private static Dictionary<string, AttributeValue> MapToItem(Consumer consumer)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue($"CONSUMER#{consumer.Id}"),
            ["SK"] = new AttributeValue("META"),
            ["EntityType"] = new AttributeValue("Consumer"),
            ["DisplayName"] = new AttributeValue(consumer.DisplayName),
            ["Adapter"] = new AttributeValue(consumer.Adapter),
            ["TargetPath"] = new AttributeValue(consumer.TargetPath),
            ["TriggerMode"] = new AttributeValue(consumer.TriggerMode),
            ["State"] = new AttributeValue(consumer.State),
            ["CreatedAt"] = new AttributeValue(consumer.CreatedAt.ToString("O")),
            ["UpdatedAt"] = new AttributeValue(consumer.UpdatedAt.ToString("O")),
        };

        if (consumer.SubsetFilter is not null)
            item["SubsetFilter"] = new AttributeValue(
                JsonSerializer.Serialize(consumer.SubsetFilter)
            );

        return item;
    }

    private static Consumer? MapFromItem(Dictionary<string, AttributeValue> item)
    {
        var id = item["PK"].S.Replace("CONSUMER#", "");

        ConsumerSubsetFilter? subsetFilter = null;
        if (item.TryGetValue("SubsetFilter", out var filterAttr))
        {
            try
            {
                subsetFilter = JsonSerializer.Deserialize<ConsumerSubsetFilter>(filterAttr.S);
            }
            catch
            {
                // Best-effort deserialization
            }
        }

        var createdAt = DateTime.TryParse(item["CreatedAt"].S, out var ca) ? ca : DateTime.MinValue;
        var updatedAt = DateTime.TryParse(item["UpdatedAt"].S, out var ua) ? ua : DateTime.MinValue;

        return Consumer
            .Create(
                id,
                item["DisplayName"].S,
                item["Adapter"].S,
                item["TargetPath"].S,
                createdAt,
                updatedAt,
                subsetFilter,
                item["TriggerMode"].S,
                item["State"].S
            )
            .Value;
    }
}
