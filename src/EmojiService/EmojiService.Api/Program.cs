using EmojiService.Api.Routes.Alias.v1;
using EmojiService.Api.Routes.Assets.v1;
using EmojiService.Api.Routes.Audit.v1;
using EmojiService.Api.Routes.Batch.v1;
using EmojiService.Api.Routes.Consumers.v1;
using EmojiService.Api.Routes.Emoji.v1;
using EmojiService.Api.Routes.SyncResults.v1;
using EmojiService.Domain;
using EmojiService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var aliasPolicyPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "Configuration",
    "alias-policy.yaml"
);
builder.Services.AddInfrastructure(builder.Configuration, aliasPolicyPath);

var app = builder.Build();

app.MapGet("/", () => "Emoji API Service");

var v1 = app.MapGroup("/api/v1");
var emojiGroup = v1.MapGroup("/emoji");

CreateEmojiRoute.Registration.Map(emojiGroup);
GetEmojiRoute.Registration.Map(emojiGroup);
SearchEmojiRoute.Registration.Map(emojiGroup);
GetEmojiAuditRoute.Registration.Map(emojiGroup);
UpdateEmojiStateRoute.Registration.Map(emojiGroup);
AddAliasRoute.Registration.Map(emojiGroup);
RetireAliasRoute.Registration.Map(emojiGroup);
SetPrimaryAliasRoute.Registration.Map(emojiGroup);
UploadEmojiRoute.Registration.Map(emojiGroup);
GetEmojiByAliasRoute.Registration.Map(emojiGroup);

var assetsGroup = app.MapGroup("/assets");
GetAssetRoute.Registration.Map(assetsGroup);

var auditGroup = v1.MapGroup("/audit");
GetAuditRoute.Registration.Map(auditGroup);

var aliasGroup = v1.MapGroup("/aliases");
RemapAliasRoute.Registration.Map(aliasGroup);

var batchGroup = v1.MapGroup("/batch");
ValidateBatchRoute.Registration.Map(batchGroup);
ApplyBatchRoute.Registration.Map(batchGroup);

var consumersGroup = v1.MapGroup("/consumers");
CreateConsumerRoute.Registration.Map(consumersGroup);
ListConsumersRoute.Registration.Map(consumersGroup);
GetConsumerRoute.Registration.Map(consumersGroup);
UpdateConsumerRoute.Registration.Map(consumersGroup);
DeleteConsumerRoute.Registration.Map(consumersGroup);
TriggerSyncRoute.Registration.Map(consumersGroup);
ListSyncResultsRoute.Registration.Map(consumersGroup);
GetSyncResultRoute.Registration.Map(v1);

var index = app.Services.GetRequiredService<IEmojiIndex>();
var repo = app.Services.GetRequiredService<IEmojiRepository>();
var allEmoji = await repo.ScanAllAsync();
foreach (var emoji in allEmoji)
{
    index.Upsert(emoji);
}

app.Run();
