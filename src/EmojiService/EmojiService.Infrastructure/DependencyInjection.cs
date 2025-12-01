using Amazon.DynamoDBv2;
using EmojiService.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmojiService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string? aliasPolicyConfigPath = null
    )
    {
        var dynamoOptions =
            configuration.GetSection(DynamoDbOptions.Section).Get<DynamoDbOptions>()
            ?? new DynamoDbOptions();

        var assetOptions =
            configuration.GetSection(AssetStoreOptions.Section).Get<AssetStoreOptions>()
            ?? new AssetStoreOptions();

        services.AddSingleton(dynamoOptions);
        services.AddSingleton(assetOptions);

        services.AddSingleton<IAmazonDynamoDB>(_ =>
        {
            var config = new AmazonDynamoDBConfig { ServiceURL = dynamoOptions.ServiceUrl };
            return new AmazonDynamoDBClient("fake", "fake", config);
        });

        services.AddSingleton<IEmojiRepository, DynamoDbEmojiRepository>();
        services.AddSingleton<IConsumerRepository, DynamoDbConsumerRepository>();
        services.AddSingleton<IEmojiIndex, InMemoryEmojiIndex>();
        services.AddSingleton<IAssetStore, LocalAssetStore>();
        services.AddSingleton<IAuditLog, DynamoDbAuditLog>();

        services.AddSingleton<IManifestGenerator, ManifestGenerator>();
        services.AddSingleton<IPropagationAdapter, LocalFilesystemPropagationAdapter>();

        services.AddSingleton<ISyncResultRepository, DynamoDbSyncResultRepository>();
        services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();

        var policyPath =
            aliasPolicyConfigPath
            ?? Path.Combine(AppContext.BaseDirectory, "Configuration", "alias-policy.yaml");
        services.AddSingleton<IAliasPolicyService>(new AliasPolicyService(policyPath));

        return services;
    }
}
