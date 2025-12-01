using EmojiService.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EmojiService.Infrastructure;

public class AliasPolicyService : IAliasPolicyService
{
    private readonly Dictionary<string, string> _reserved = new();
    private readonly List<(string Prefix, string Reason)> _reservedPrefixes = [];
    private readonly Dictionary<string, string> _blocked = new();

    public AliasPolicyService(string configPath)
    {
        if (!File.Exists(configPath))
            return;

        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<AliasPolicyConfig>(yaml);

        if (config?.Reserved is not null)
        {
            foreach (var entry in config.Reserved)
                _reserved[entry.Name.ToLowerInvariant()] = entry.Reason;
        }

        if (config?.ReservedPrefixes is not null)
        {
            foreach (var entry in config.ReservedPrefixes)
                _reservedPrefixes.Add((entry.Prefix.ToLowerInvariant(), entry.Reason));
        }

        if (config?.Blocked is not null)
        {
            foreach (var entry in config.Blocked)
                _blocked[entry.Name.ToLowerInvariant()] = entry.Reason;
        }
    }

    public AliasPolicyResult CheckName(string name)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant();

        if (_blocked.TryGetValue(normalized, out var blockReason))
            return new AliasPolicyResult(AliasPolicyStatus.Blocked, blockReason);

        if (_reserved.TryGetValue(normalized, out var reservedReason))
            return new AliasPolicyResult(AliasPolicyStatus.Reserved, reservedReason);

        foreach (var (prefix, reason) in _reservedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                return new AliasPolicyResult(AliasPolicyStatus.Reserved, reason);
        }

        return new AliasPolicyResult(AliasPolicyStatus.Allowed, null);
    }

    private sealed class AliasPolicyConfig
    {
        public List<PolicyEntry>? Reserved { get; set; }
        public List<PrefixEntry>? ReservedPrefixes { get; set; }
        public List<PolicyEntry>? Blocked { get; set; }
    }

    private sealed class PolicyEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    private sealed class PrefixEntry
    {
        public string Prefix { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
