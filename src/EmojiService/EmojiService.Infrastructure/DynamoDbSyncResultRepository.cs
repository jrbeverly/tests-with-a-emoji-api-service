using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class DynamoDbSyncResultRepository : ISyncResultRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private static readonly JsonSerializerOptions CursorJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DynamoDbSyncResultRepository(IAmazonDynamoDB dynamoDb, DynamoDbOptions options)
    {
        _dynamoDb = dynamoDb;
        _tableName = options.TableName;
    }

    public async Task<Result> SaveAsync(SyncResultRecord record)
    {
        try
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"SYNCRESULT#{record.ResultId}"),
                ["SK"] = new AttributeValue("META"),
                ["EntityType"] = new AttributeValue("SyncResult"),
                ["GSI1PK"] = new AttributeValue($"CONSUMER#{record.ConsumerId}"),
                ["GSI1SK"] = new AttributeValue(
                    $"SYNCRESULT#{record.StartedAt:O}#{record.ResultId}"
                ),
                ["ConsumerId"] = new AttributeValue(record.ConsumerId),
                ["ManifestId"] = new AttributeValue(record.ManifestId),
                ["Status"] = new AttributeValue(record.Status),
                ["TotalEmoji"] = new AttributeValue { N = record.TotalEmoji.ToString() },
                ["SyncedEmoji"] = new AttributeValue { N = record.SyncedEmoji.ToString() },
                ["SkippedEmoji"] = new AttributeValue { N = record.SkippedEmoji.ToString() },
                ["FailedEmoji"] = new AttributeValue { N = record.FailedEmoji.ToString() },
                ["StartedAt"] = new AttributeValue(record.StartedAt.ToString("O")),
                ["CompletedAt"] = new AttributeValue(record.CompletedAt.ToString("O")),
            };

            if (record.ErrorMessage is not null)
                item["ErrorMessage"] = new AttributeValue(record.ErrorMessage);

            await _dynamoDb.PutItemAsync(
                new PutItemRequest { TableName = _tableName, Item = item }
            );

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("E760", $"Failed to save sync result: {ex.Message}"));
        }
    }

    public async Task<SyncResultRecord?> GetByIdAsync(string resultId)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"SYNCRESULT#{resultId}"),
                ["SK"] = new AttributeValue("META"),
            },
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item.Count == 0 || !response.IsItemSet)
            return null;

        return MapFromItem(response.Item);
    }

    public async Task<SyncResultPage> ListByConsumerAsync(
        string consumerId,
        string? cursor,
        int limit
    )
    {
        var keyConditionExpression = "GSI1PK = :gsi1pk AND begins_with(GSI1SK, :gsi1sk)";
        var expressionValues = new Dictionary<string, AttributeValue>
        {
            [":gsi1pk"] = new AttributeValue($"CONSUMER#{consumerId}"),
            [":gsi1sk"] = new AttributeValue("SYNCRESULT#"),
        };

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (cursor is not null)
            exclusiveStartKey = DecodeExclusiveStartKey(cursor);

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "GSI1",
            KeyConditionExpression = keyConditionExpression,
            ExpressionAttributeValues = expressionValues,
            Limit = limit + 1,
            ScanIndexForward = false,
            ExclusiveStartKey = exclusiveStartKey,
        };

        try
        {
            var response = await _dynamoDb.QueryAsync(request);

            var items = response.Items.Select(MapFromItem).ToList();
            var hasMore = items.Count > limit;

            if (hasMore)
                items = items.Take(limit).ToList();

            string? nextCursor = null;
            if (hasMore && response.LastEvaluatedKey.Count > 0)
                nextCursor = EncodeExclusiveStartKey(response.LastEvaluatedKey);

            return new SyncResultPage
            {
                Items = items,
                Cursor = nextCursor,
                HasMore = hasMore,
            };
        }
        catch (ResourceNotFoundException)
        {
            return new SyncResultPage
            {
                Items = Array.Empty<SyncResultRecord>(),
                Cursor = null,
                HasMore = false,
            };
        }
    }

    private static SyncResultRecord MapFromItem(Dictionary<string, AttributeValue> item)
    {
        var resultId = item["PK"].S.Replace("SYNCRESULT#", "");

        return new SyncResultRecord
        {
            ResultId = resultId,
            ConsumerId = item["ConsumerId"].S,
            ManifestId = item["ManifestId"].S,
            Status = item["Status"].S,
            TotalEmoji = int.Parse(item["TotalEmoji"].N),
            SyncedEmoji = int.Parse(item["SyncedEmoji"].N),
            SkippedEmoji = int.Parse(item["SkippedEmoji"].N),
            FailedEmoji = int.Parse(item["FailedEmoji"].N),
            ErrorMessage = item.TryGetValue("ErrorMessage", out var em) ? em.S : null,
            StartedAt = DateTime.TryParse(item["StartedAt"].S, out var sa) ? sa : DateTime.MinValue,
            CompletedAt = DateTime.TryParse(item["CompletedAt"].S, out var ca)
                ? ca
                : DateTime.MinValue,
        };
    }

    private static string EncodeExclusiveStartKey(Dictionary<string, AttributeValue> key)
    {
        // Convert to simple string-to-string dict for JSON serialization
        var serializable = new Dictionary<string, string>();
        foreach (var (k, v) in key)
        {
            var value = v.S ?? v.N ?? string.Empty;
            serializable[k] = value;
        }

        var json = JsonSerializer.Serialize(serializable, CursorJsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, AttributeValue> DecodeExclusiveStartKey(string cursor)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, CursorJsonOptions)!;

        var key = new Dictionary<string, AttributeValue>();
        foreach (var (k, v) in dict)
        {
            // Determine if value is numeric (for N-type attributes)
            if (k is "TotalEmoji" or "SyncedEmoji" or "SkippedEmoji" or "FailedEmoji")
                key[k] = new AttributeValue { N = v };
            else
                key[k] = new AttributeValue(v);
        }

        return key;
    }
}
