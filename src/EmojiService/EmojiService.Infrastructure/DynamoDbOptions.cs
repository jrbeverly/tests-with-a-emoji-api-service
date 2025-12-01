namespace EmojiService.Infrastructure;

public sealed record DynamoDbOptions
{
    public const string Section = "DynamoDB";

    public string ServiceUrl { get; init; } = "http://localhost:8000";
    public string TableName { get; init; } = "emoji-registry";
    public string Region { get; init; } = "us-east-1";
}
