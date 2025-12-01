using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

public class DynamoDbAuditLog : IAuditLog
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbAuditLog(IAmazonDynamoDB dynamoDb, DynamoDbOptions options)
    {
        _dynamoDb = dynamoDb;
        _tableName = options.TableName;
    }

    public async Task WriteAsync(AuditEvent @event)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue($"EMOJI#{@event.SubjectUid}"),
            ["SK"] = new AttributeValue($"AUDIT#{@event.EventId}"),
            ["EntityType"] = new AttributeValue("AuditEvent"),
            ["GSI1PK"] = new AttributeValue("AUDIT"),
            ["GSI1SK"] = new AttributeValue($"{@event.OccurredAt:O}#{@event.EventId}"),
            ["Data"] = new AttributeValue(JsonSerializer.Serialize(@event)),
            ["CreatedAt"] = new AttributeValue(@event.OccurredAt.ToString("O")),
        };

        await _dynamoDb.PutItemAsync(new PutItemRequest { TableName = _tableName, Item = item });
    }

    public async Task<AuditPage> GetForEmojiAsync(string uid, int limit, string? cursor)
    {
        var keyConditionExpression = "PK = :pk AND begins_with(SK, :sk_prefix)";
        var expressionValues = new Dictionary<string, AttributeValue>
        {
            [":pk"] = new AttributeValue($"EMOJI#{uid}"),
            [":sk_prefix"] = new AttributeValue("AUDIT#"),
        };

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (cursor is not null)
        {
            var cursorData = DecodeCursor(cursor);
            exclusiveStartKey = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"EMOJI#{uid}"),
                ["SK"] = new AttributeValue($"AUDIT#{cursorData.EventId}"),
            };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = keyConditionExpression,
            ExpressionAttributeValues = expressionValues,
            Limit = limit + 1,
            ScanIndexForward = false,
            ExclusiveStartKey = exclusiveStartKey,
        };

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items.Select(MapFromItem).ToList();
        var hasMore = items.Count > limit;

        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var lastEventId = items[^1].EventId;
            nextCursor = EncodeCursor(new CursorData(lastEventId, null));
        }

        return new AuditPage
        {
            Items = items,
            Cursor = nextCursor,
            HasMore = hasMore,
        };
    }

    public async Task<AuditPage> GetRecentAsync(int limit, string? cursor)
    {
        var keyConditionExpression = "GSI1PK = :gsi1pk";
        var expressionValues = new Dictionary<string, AttributeValue>
        {
            [":gsi1pk"] = new AttributeValue("AUDIT"),
        };

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (cursor is not null)
        {
            var cursorData = DecodeCursor(cursor);
            exclusiveStartKey = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"EMOJI#{cursorData.SubjectUid}"),
                ["SK"] = new AttributeValue($"AUDIT#{cursorData.EventId}"),
                ["GSI1PK"] = new AttributeValue("AUDIT"),
                ["GSI1SK"] = new AttributeValue(cursorData.Gsi1Sk!),
            };
        }

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

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items.Select(MapFromItem).ToList();
        var hasMore = items.Count > limit;

        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = EncodeCursor(
                new CursorData(last.EventId, $"{last.OccurredAt:O}#{last.EventId}", last.SubjectUid)
            );
        }

        return new AuditPage
        {
            Items = items,
            Cursor = nextCursor,
            HasMore = hasMore,
        };
    }

    private static AuditEvent MapFromItem(Dictionary<string, AttributeValue> item)
    {
        var data = item["Data"].S;
        return JsonSerializer.Deserialize<AuditEvent>(data)!;
    }

    private sealed record CursorData(string EventId, string? Gsi1Sk, string? SubjectUid = null);

    private static string EncodeCursor(CursorData data)
    {
        var json = JsonSerializer.Serialize(data);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static CursorData DecodeCursor(string cursor)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        return JsonSerializer.Deserialize<CursorData>(json)!;
    }
}
