using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EmojiService.Domain;

namespace EmojiService.Infrastructure;

// Key schema per ADR-0006 (v1 single-table design):
//   Emoji: PK=EMOJI#{uid}, SK=META
//   Alias: PK=ALIAS#{name}, SK=META, GSI1PK=EMOJI#{uid}, GSI1SK=ALIAS#{name}

public class DynamoDbEmojiRepository : IEmojiRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IAliasPolicyService _policy;

    public DynamoDbEmojiRepository(
        IAmazonDynamoDB dynamoDb,
        DynamoDbOptions options,
        IAliasPolicyService policy
    )
    {
        _dynamoDb = dynamoDb;
        _tableName = options.TableName;
        _policy = policy;
    }

    public async Task<Result> SaveAsync(Emoji emoji)
    {
        var allAliasNames = new List<string> { emoji.PrimaryAlias.Value };
        allAliasNames.AddRange(emoji.SecondaryAliases);

        foreach (var aliasName in allAliasNames)
        {
            var policyResult = _policy.CheckName(aliasName);
            if (policyResult.Status == AliasPolicyStatus.Blocked)
                return Result.Failure(
                    new Error("E601", $"Alias '{aliasName}' is blocked: {policyResult.Reason}")
                );
            if (policyResult.Status == AliasPolicyStatus.Reserved)
                return Result.Failure(
                    new Error("E600", $"Alias '{aliasName}' is reserved: {policyResult.Reason}")
                );
        }

        foreach (var aliasName in allAliasNames)
        {
            var existingAlias = await GetAliasItemAsync(aliasName);
            if (existingAlias is not null)
            {
                var existingEmojiUid = GetAliasEmojiUid(existingAlias);
                if (existingEmojiUid is not null && existingEmojiUid != emoji.Uid.Value)
                {
                    var existingState = GetAliasState(existingAlias);
                    if (existingState == "active")
                        return Result.Failure(
                            new Error(
                                "E501",
                                $"Alias '{aliasName}' is already in use by emoji '{existingEmojiUid}'"
                            )
                        );
                }
            }
        }

        try
        {
            await _dynamoDb.PutItemAsync(
                new PutItemRequest { TableName = _tableName, Item = MapToItem(emoji) }
            );

            await _dynamoDb.PutItemAsync(
                new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MapAliasToItem(
                        emoji.PrimaryAlias.Value,
                        emoji.Uid.Value,
                        isPrimary: true
                    ),
                }
            );

            foreach (var secondary in emoji.SecondaryAliases)
            {
                await _dynamoDb.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = _tableName,
                        Item = MapAliasToItem(secondary, emoji.Uid.Value, isPrimary: false),
                    }
                );
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("E500", $"Failed to save emoji: {ex.Message}"));
        }
    }

    public async Task<Emoji?> GetByUidAsync(EmojiUid uid)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"EMOJI#{uid.Value}"),
                ["SK"] = new AttributeValue("META"),
            },
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item.Count == 0 || !response.IsItemSet)
            return null;

        return MapFromItem(response.Item);
    }

    public async Task<Emoji?> GetByAliasAsync(string aliasName)
    {
        // ADR-0006: alias → emoji is a two-step GetItem:
        //   1. GetItem PK=ALIAS#{name}, SK=META  →  extract emoji_uid
        //   2. GetItem PK=EMOJI#{uid}, SK=META

        var aliasItem = await GetAliasItemAsync(aliasName);
        if (aliasItem is null)
            return null;

        var emojiUid = GetAliasEmojiUid(aliasItem);
        if (emojiUid is null)
            return null;

        var uidResult = EmojiUid.Create(emojiUid);
        if (uidResult.IsFailure)
            return null;

        return await GetByUidAsync(uidResult.Value!);
    }

    public async Task<IReadOnlyList<AliasRecord>> ListAliasesForEmojiAsync(string uid)
    {
        // ADR-0006: Query GSI1 where GSI1PK=EMOJI#{uid} AND begins_with(GSI1SK, "ALIAS#")
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "GSI1",
            KeyConditionExpression = "GSI1PK = :pk AND begins_with(GSI1SK, :sk_prefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new AttributeValue($"EMOJI#{uid}"),
                [":sk_prefix"] = new AttributeValue("ALIAS#"),
            },
        };

        try
        {
            var response = await _dynamoDb.QueryAsync(request);

            var records = new List<AliasRecord>();
            foreach (var item in response.Items)
            {
                var name = GetAliasName(item);
                var emojiUid = GetAliasEmojiUid(item);
                if (name is not null && emojiUid is not null)
                    records.Add(new AliasRecord { Name = name, EmojiUid = emojiUid });
            }

            return records;
        }
        catch (ResourceNotFoundException)
        {
            return Array.Empty<AliasRecord>();
        }
    }

    public async Task<IReadOnlyList<Emoji>> ScanAllAsync()
    {
        var emojis = new List<Emoji>();
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;

        do
        {
            var request = new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "EntityType = :type",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":type"] = new AttributeValue("Emoji"),
                },
                ExclusiveStartKey = exclusiveStartKey,
            };

            try
            {
                var response = await _dynamoDb.ScanAsync(request);

                foreach (var item in response.Items)
                {
                    var emoji = MapFromItem(item);
                    if (emoji is not null)
                        emojis.Add(emoji);
                }

                exclusiveStartKey =
                    response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
            }
            catch (ResourceNotFoundException)
            {
                break;
            }
        } while (exclusiveStartKey is not null);

        return emojis;
    }

    public async Task<Result> AddAliasAsync(EmojiUid emojiUid, string aliasName)
    {
        var aliasResult = Alias.Create(aliasName);
        if (aliasResult.IsFailure)
            return Result.Failure(aliasResult.Error!);

        var normalizedName = aliasResult.Value!.Value;

        var policyResult = _policy.CheckName(normalizedName);
        if (policyResult.Status == AliasPolicyStatus.Blocked)
            return Result.Failure(
                new Error("E601", $"Alias '{normalizedName}' is blocked: {policyResult.Reason}")
            );
        if (policyResult.Status == AliasPolicyStatus.Reserved)
            return Result.Failure(
                new Error("E600", $"Alias '{normalizedName}' is reserved: {policyResult.Reason}")
            );

        var emoji = await GetByUidAsync(emojiUid);
        if (emoji is null)
            return Result.Failure(
                new Error("E510", $"Emoji with UID '{emojiUid.Value}' not found")
            );

        if (emoji.PrimaryAlias.Value == normalizedName)
            return Result.Failure(
                new Error("E511", $"Alias '{normalizedName}' is already the primary alias")
            );

        if (emoji.SecondaryAliases.Contains(normalizedName))
            return Result.Failure(
                new Error("E512", $"Alias '{normalizedName}' is already a secondary alias")
            );

        var existingAlias = await GetAliasItemAsync(normalizedName);
        if (existingAlias is not null)
        {
            var existingEmojiUid = GetAliasEmojiUid(existingAlias);
            if (existingEmojiUid is not null && existingEmojiUid != emojiUid.Value)
            {
                var existingState = GetAliasState(existingAlias);
                if (existingState == "active")
                    return Result.Failure(
                        new Error(
                            "E513",
                            $"Alias '{normalizedName}' is already in use by emoji '{existingEmojiUid}'"
                        )
                    );
            }
        }

        var updatedEmoji = emoji with
        {
            SecondaryAliases = emoji.SecondaryAliases.Append(normalizedName).ToList(),
            UpdatedAt = DateTime.UtcNow,
        };

        await _dynamoDb.PutItemAsync(
            new PutItemRequest { TableName = _tableName, Item = MapToItem(updatedEmoji) }
        );

        await _dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = _tableName,
                Item = MapAliasToItem(normalizedName, emojiUid.Value, isPrimary: false),
            }
        );

        return Result.Success();
    }

    public async Task<Result> RetireAliasAsync(EmojiUid emojiUid, string aliasName)
    {
        var aliasResult = Alias.Create(aliasName);
        if (aliasResult.IsFailure)
            return Result.Failure(aliasResult.Error!);

        var normalizedName = aliasResult.Value!.Value;
        var emoji = await GetByUidAsync(emojiUid);
        if (emoji is null)
            return Result.Failure(
                new Error("E520", $"Emoji with UID '{emojiUid.Value}' not found")
            );

        if (emoji.PrimaryAlias.Value == normalizedName)
            return Result.Failure(new Error("E521", "Cannot retire the primary alias"));

        if (!emoji.SecondaryAliases.Contains(normalizedName))
            return Result.Failure(
                new Error("E522", $"Alias '{normalizedName}' is not an active secondary alias")
            );

        var existingAlias = await GetAliasItemAsync(normalizedName);
        if (existingAlias is null)
            return Result.Failure(new Error("E523", $"Alias '{normalizedName}' not found"));

        var updatedEmoji = emoji with
        {
            SecondaryAliases = emoji.SecondaryAliases.Where(a => a != normalizedName).ToList(),
            UpdatedAt = DateTime.UtcNow,
        };

        await _dynamoDb.PutItemAsync(
            new PutItemRequest { TableName = _tableName, Item = MapToItem(updatedEmoji) }
        );

        await _dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = _tableName,
                Item = MapAliasToItem(
                    normalizedName,
                    emojiUid.Value,
                    isPrimary: false,
                    state: "retired"
                ),
            }
        );

        return Result.Success();
    }

    public async Task<Result> SetPrimaryAliasAsync(EmojiUid emojiUid, string aliasName)
    {
        var aliasResult = Alias.Create(aliasName);
        if (aliasResult.IsFailure)
            return Result.Failure(aliasResult.Error!);

        var normalizedName = aliasResult.Value!.Value;
        var emoji = await GetByUidAsync(emojiUid);
        if (emoji is null)
            return Result.Failure(
                new Error("E530", $"Emoji with UID '{emojiUid.Value}' not found")
            );

        if (emoji.PrimaryAlias.Value == normalizedName)
            return Result.Failure(
                new Error("E531", $"Alias '{normalizedName}' is already the primary alias")
            );

        var isSecondary = emoji.SecondaryAliases.Contains(normalizedName);
        if (!isSecondary)
            return Result.Failure(
                new Error("E532", $"Alias '{normalizedName}' is not an active alias of this emoji")
            );

        var oldPrimaryName = emoji.PrimaryAlias.Value;

        var newSecondaryAliases = emoji
            .SecondaryAliases.Where(a => a != normalizedName)
            .Append(oldPrimaryName)
            .ToList();

        var updatedEmoji = emoji with
        {
            PrimaryAlias = aliasResult.Value!,
            SecondaryAliases = newSecondaryAliases,
            UpdatedAt = DateTime.UtcNow,
        };

        await _dynamoDb.PutItemAsync(
            new PutItemRequest { TableName = _tableName, Item = MapToItem(updatedEmoji) }
        );

        await _dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = _tableName,
                Item = MapAliasToItem(oldPrimaryName, emojiUid.Value, isPrimary: false),
            }
        );

        await _dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = _tableName,
                Item = MapAliasToItem(normalizedName, emojiUid.Value, isPrimary: true),
            }
        );

        return Result.Success();
    }

    public async Task<Result> RemapAliasAsync(string aliasName, EmojiUid targetEmojiUid)
    {
        return await RemapCoreAsync(aliasName, targetEmojiUid, skipProtectionCheck: false);
    }

    public async Task<Result> RemapAliasWithOverrideAsync(string aliasName, EmojiUid targetEmojiUid)
    {
        return await RemapCoreAsync(aliasName, targetEmojiUid, skipProtectionCheck: true);
    }

    private async Task<Result> RemapCoreAsync(
        string aliasName,
        EmojiUid targetEmojiUid,
        bool skipProtectionCheck
    )
    {
        var aliasResult = Alias.Create(aliasName);
        if (aliasResult.IsFailure)
            return Result.Failure(aliasResult.Error!);

        var normalizedName = aliasResult.Value!.Value;

        var policyResult = _policy.CheckName(normalizedName);
        if (policyResult.Status == AliasPolicyStatus.Blocked)
            return Result.Failure(
                new Error("E601", $"Alias '{normalizedName}' is blocked: {policyResult.Reason}")
            );

        var aliasItem = await GetAliasItemAsync(normalizedName);
        if (aliasItem is null)
            return Result.Failure(new Error("E544", $"Alias '{normalizedName}' not found"));

        var currentEmojiUid = GetAliasEmojiUid(aliasItem);
        if (currentEmojiUid is null)
            return Result.Failure(
                new Error("E544", $"Alias '{normalizedName}' has no emoji mapping")
            );

        if (!skipProtectionCheck)
        {
            var protection = GetAliasProtection(aliasItem);
            if (protection == "protected")
                return Result.Failure(
                    new Error(
                        "E543",
                        $"Alias '{normalizedName}' is protected and cannot be remapped through the standard path"
                    )
                );
        }

        if (currentEmojiUid == targetEmojiUid.Value)
            return Result.Failure(
                new Error(
                    "E545",
                    $"Alias '{normalizedName}' already belongs to emoji '{currentEmojiUid}'"
                )
            );

        var sourceEmoji = await GetByUidAsync(EmojiUid.Create(currentEmojiUid).Value!);
        if (sourceEmoji is null)
            return Result.Failure(new Error("E542", $"Source emoji '{currentEmojiUid}' not found"));

        var targetEmoji = await GetByUidAsync(targetEmojiUid);
        if (targetEmoji is null)
            return Result.Failure(
                new Error("E541", $"Target emoji '{targetEmojiUid.Value}' not found")
            );

        if (targetEmoji.PrimaryAlias.Value == normalizedName)
            return Result.Failure(
                new Error(
                    "E545",
                    $"Alias '{normalizedName}' is already the primary alias of target emoji"
                )
            );

        if (targetEmoji.SecondaryAliases.Contains(normalizedName))
            return Result.Failure(
                new Error(
                    "E545",
                    $"Alias '{normalizedName}' is already a secondary alias of target emoji"
                )
            );

        var isPrimary = sourceEmoji.PrimaryAlias.Value == normalizedName;
        var transactItems = new List<TransactWriteItem>();
        var now = DateTime.UtcNow;

        // Build updated source emoji
        Emoji updatedSourceEmoji;
        if (isPrimary)
        {
            if (sourceEmoji.SecondaryAliases.Count == 0)
                return Result.Failure(
                    new Error(
                        "E547",
                        $"Cannot remap '{normalizedName}': it is the only alias of emoji '{sourceEmoji.Uid.Value}'"
                    )
                );

            var promotedSecondary = sourceEmoji.SecondaryAliases[0];
            var newPrimaryResult = Alias.Create(promotedSecondary);
            if (newPrimaryResult.IsFailure)
                return Result.Failure(
                    new Error(
                        "E547",
                        $"Cannot remap '{normalizedName}': failed to promote secondary alias"
                    )
                );

            updatedSourceEmoji = sourceEmoji with
            {
                PrimaryAlias = newPrimaryResult.Value!,
                SecondaryAliases = sourceEmoji.SecondaryAliases.Skip(1).ToList(),
                UpdatedAt = now,
            };

            transactItems.Add(
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = _tableName,
                        Item = MapAliasToItem(
                            promotedSecondary,
                            sourceEmoji.Uid.Value,
                            isPrimary: true
                        ),
                    },
                }
            );
        }
        else
        {
            updatedSourceEmoji = sourceEmoji with
            {
                SecondaryAliases = sourceEmoji
                    .SecondaryAliases.Where(a => a != normalizedName)
                    .ToList(),
                UpdatedAt = now,
            };
        }

        var updatedTargetEmoji = targetEmoji with
        {
            SecondaryAliases = targetEmoji.SecondaryAliases.Append(normalizedName).ToList(),
            UpdatedAt = now,
        };

        transactItems.Add(
            new TransactWriteItem
            {
                Put = new Put { TableName = _tableName, Item = MapToItem(updatedSourceEmoji) },
            }
        );

        transactItems.Add(
            new TransactWriteItem
            {
                Put = new Put { TableName = _tableName, Item = MapToItem(updatedTargetEmoji) },
            }
        );

        transactItems.Add(
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _tableName,
                    Item = MapAliasToItem(normalizedName, targetEmojiUid.Value, isPrimary: false),
                },
            }
        );

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = transactItems }
            );

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("E548", $"Remap failed: {ex.Message}"));
        }
    }

    private async Task<Dictionary<string, AttributeValue>?> GetAliasItemAsync(string aliasName)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue($"ALIAS#{aliasName}"),
                ["SK"] = new AttributeValue("META"),
            },
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item.Count == 0 || !response.IsItemSet)
            return null;

        return response.Item;
    }

    private static string? GetAliasState(Dictionary<string, AttributeValue> item)
    {
        if (item.TryGetValue("Data", out var dataAttr))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataAttr.S);
                if (doc.RootElement.TryGetProperty("state", out var stateProp))
                    return stateProp.GetString();
            }
            catch
            {
                // Data payload is best-effort
            }
        }

        return "active";
    }

    private static string? GetAliasProtection(Dictionary<string, AttributeValue> item)
    {
        if (item.TryGetValue("Data", out var dataAttr))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataAttr.S);
                if (doc.RootElement.TryGetProperty("protection", out var protProp))
                    return protProp.GetString();
            }
            catch
            {
                // Data payload is best-effort
            }
        }

        return null;
    }

    private static string? GetAliasEmojiUid(Dictionary<string, AttributeValue> item)
    {
        if (item.TryGetValue("GSI1PK", out var gsi1Pk))
        {
            var value = gsi1Pk.S;
            if (value.StartsWith("EMOJI#"))
                return value["EMOJI#".Length..];
        }

        // Fallback: parse from Data JSON
        if (item.TryGetValue("Data", out var dataAttr))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataAttr.S);
                if (doc.RootElement.TryGetProperty("emoji_uid", out var uidProp))
                    return uidProp.GetString();
            }
            catch
            {
                // Data payload is best-effort
            }
        }

        return null;
    }

    private static string? GetAliasName(Dictionary<string, AttributeValue> item)
    {
        if (item.TryGetValue("PK", out var pk))
        {
            var value = pk.S;
            if (value.StartsWith("ALIAS#"))
                return value["ALIAS#".Length..];
        }

        return null;
    }

    private static Dictionary<string, AttributeValue> MapToItem(Emoji emoji)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue($"EMOJI#{emoji.Uid.Value}"),
            ["SK"] = new AttributeValue("META"),
            ["EntityType"] = new AttributeValue("Emoji"),
            ["PrimaryAlias"] = new AttributeValue(emoji.PrimaryAlias.Value),
            ["DisplayName"] = new AttributeValue(emoji.DisplayName),
            ["Description"] = new AttributeValue(emoji.Description),
            ["ContentType"] = new AttributeValue(emoji.ContentType),
            ["AssetReference"] = new AttributeValue(emoji.AssetReference),
            ["Owner"] = new AttributeValue(emoji.Owner),
            ["State"] = new AttributeValue(emoji.LifecycleState.Value),
            ["CreatedAt"] = new AttributeValue(emoji.CreatedAt.ToString("O")),
            ["UpdatedAt"] = new AttributeValue(emoji.UpdatedAt.ToString("O")),
        };

        if (emoji.Tags.Count > 0)
            item["Tags"] = new AttributeValue { SS = emoji.Tags.ToList() };

        if (emoji.Categories.Count > 0)
            item["Categories"] = new AttributeValue { SS = emoji.Categories.ToList() };

        if (emoji.SecondaryAliases.Count > 0)
            item["SecondaryAliases"] = new AttributeValue { SS = emoji.SecondaryAliases.ToList() };

        return item;
    }

    private static Dictionary<string, AttributeValue> MapAliasToItem(
        string aliasName,
        string emojiUid,
        bool isPrimary = true,
        string state = "active"
    )
    {
        // ADR-0006 alias key layout:
        //   PK=ALIAS#{name}, SK=META, GSI1PK=EMOJI#{uid}, GSI1SK=ALIAS#{name}
        return new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue($"ALIAS#{aliasName}"),
            ["SK"] = new AttributeValue("META"),
            ["EntityType"] = new AttributeValue("Alias"),
            ["GSI1PK"] = new AttributeValue($"EMOJI#{emojiUid}"),
            ["GSI1SK"] = new AttributeValue($"ALIAS#{aliasName}"),
            ["Data"] = new AttributeValue(
                JsonSerializer.Serialize(
                    new
                    {
                        name = aliasName,
                        emoji_uid = emojiUid,
                        is_primary = isPrimary,
                        state,
                        protection = "normal",
                    }
                )
            ),
        };
    }

    private static Emoji? MapFromItem(Dictionary<string, AttributeValue> item)
    {
        var uidResult = EmojiUid.Create(item["PK"].S.Replace("EMOJI#", ""));
        if (uidResult.IsFailure)
            return null;

        var aliasResult = Alias.Create(item["PrimaryAlias"].S);
        if (aliasResult.IsFailure)
            return null;

        var tags =
            item.TryGetValue("Tags", out var tagsAttr) && tagsAttr.SS.Count > 0
                ? tagsAttr.SS
                : Enumerable.Empty<string>();

        var categories =
            item.TryGetValue("Categories", out var catAttr) && catAttr.SS.Count > 0
                ? catAttr.SS
                : Enumerable.Empty<string>();

        var secondaryAliases =
            item.TryGetValue("SecondaryAliases", out var secAttr) && secAttr.SS.Count > 0
                ? secAttr.SS
                : Enumerable.Empty<string>();

        var owner = item.TryGetValue("Owner", out var ownerAttr) ? ownerAttr.S : string.Empty;
        var state = item.TryGetValue("State", out var stateAttr)
            ? LifecycleState.Create(stateAttr.S).Value
            : LifecycleState.Active;

        var createdAt = DateTime.TryParse(item["CreatedAt"].S, out var ca) ? ca : DateTime.MinValue;
        var updatedAt = DateTime.TryParse(item["UpdatedAt"].S, out var ua) ? ua : DateTime.MinValue;

        return Emoji
            .Create(
                uidResult.Value!,
                aliasResult.Value!,
                item["DisplayName"].S,
                item["Description"].S,
                item["ContentType"].S,
                item["AssetReference"].S,
                createdAt,
                updatedAt,
                tags,
                categories,
                owner,
                state,
                secondaryAliases
            )
            .Value;
    }
}
