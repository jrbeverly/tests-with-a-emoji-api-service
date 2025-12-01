using System.Text.Json;

namespace EmojiService.IntegrationTests.Fixtures;

public static class CuratedDataset
{
    public static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    public const int ItemCount = 20;

    public static readonly IReadOnlyList<ItemSpec> Items = new List<ItemSpec>
    {
        new(
            "party",
            "party-parrot",
            "Party Parrot",
            "A festive parrot",
            new[] { "party", "fun", "reaction" },
            new[] { "fun", "social" },
            "team-alpha"
        ),
        new(
            "celebrate",
            "celebrate",
            "Celebrate",
            "A celebration emoji",
            new[] { "celebrate", "happy", "party" },
            new[] { "events", "fun" },
            "team-alpha"
        ),
        new(
            "think",
            "thinking-cat",
            "Thinking Cat",
            "A thoughtful cat",
            new[] { "reaction", "funny" },
            new[] { "fun", "social" },
            "team-alpha"
        ),
        new(
            "thumbs",
            "thumbs-up",
            "Thumbs Up",
            "Thumbs up reaction",
            new[] { "reaction", "approval" },
            new[] { "work" },
            "team-beta"
        ),
        new(
            "wave",
            "wave-hello",
            "Wave Hello",
            "A waving hand",
            new[] { "reaction", "social" },
            new[] { "social" },
            "team-beta"
        ),
        new(
            "tada",
            "tada",
            "Tada",
            "Surprise celebration",
            new[] { "celebrate", "party", "fun" },
            new[] { "events", "fun" },
            "team-beta"
        ),
        new(
            "review",
            "code-review",
            "Code Review",
            "Code review approved",
            new[] { "dev", "workflow" },
            new[] { "dev-tools", "work" },
            "eng-team"
        ),
        new(
            "rocket",
            "rocket-ship",
            "Rocket Ship",
            "Blast off!",
            new[] { "dev", "fun" },
            new[] { "dev-tools", "social" },
            "eng-team"
        ),
        new(
            "bug",
            "bug-report",
            "Bug Report",
            "A bug has been found",
            new[] { "dev", "ops" },
            new[] { "dev-tools" },
            "eng-team"
        ),
        new(
            "deploy",
            "deploy",
            "Deploy",
            "Shipping to production",
            new[] { "dev", "ops", "workflow" },
            new[] { "dev-tools" },
            "eng-team"
        ),
        new(
            "fire",
            "fire-reaction",
            "Fire",
            "This is fire",
            new[] { "reaction", "fun" },
            new[] { "reactions" },
            "team-alpha"
        ),
        new(
            "heart",
            "heart-reaction",
            "Heart",
            "Love this",
            new[] { "reaction", "social" },
            new[] { "reactions", "social" },
            "team-beta"
        ),
        new(
            "check",
            "check-mark",
            "Check Mark",
            "Approved",
            new[] { "approval", "workflow" },
            new[] { "work", "approvals" },
            "eng-team"
        ),
        new(
            "cross",
            "cross-mark",
            "Cross Mark",
            "Rejected",
            new[] { "reaction", "workflow" },
            new[] { "work" },
            "eng-team"
        ),
        new(
            "hundred",
            "hundred-points",
            "Hundred Points",
            "100%",
            new[] { "reaction" },
            new[] { "reactions" },
            "team-beta"
        ),
        new(
            "coffee",
            "coffee-mug",
            "Coffee Mug",
            "Morning coffee",
            new[] { "dev", "social" },
            new[] { "dev-tools", "social" },
            "team-alpha"
        ),
        new(
            "globe",
            "globe-icon",
            "Globe",
            "Worldwide",
            new[] { "social", "fun" },
            new[] { "social" },
            "team-alpha"
        ),
        new(
            "lock",
            "lock-icon",
            "Lock Icon",
            "Secured",
            new[] { "workflow", "dev" },
            new[] { "work", "dev-tools" },
            "eng-team"
        ),
        new(
            "star",
            "star-icon-reaction",
            "Star Icon",
            "Favorite",
            new[] { "approval", "fun" },
            new[] { "approvals" },
            "team-beta"
        ),
        new(
            "megaphone",
            "megaphone-icon",
            "Megaphone",
            "Announcement",
            new[] { "workflow", "ops" },
            new[] { "work" },
            "eng-team"
        ),
    };

    public static string ManifestJson => BuildManifestJson();

    private static string BuildManifestJson()
    {
        var items = Items.Select(item => new
        {
            file_key = item.FileKey,
            alias = item.Alias,
            display_name = item.DisplayName,
            description = item.Description,
            tags = item.Tags,
            categories = item.Categories,
            owner = item.Owner,
        });

        return JsonSerializer.Serialize(
            new { items },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
            }
        );
    }

    public static MultipartFormDataContent CreateBatchContent()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(ManifestJson), "manifest");

        foreach (var item in Items)
        {
            var fileContent = new ByteArrayContent(PngBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "image/png"
            );
            content.Add(fileContent, item.FileKey, $"{item.FileKey}.png");
        }

        return content;
    }

    public sealed record ItemSpec(
        string FileKey,
        string Alias,
        string DisplayName,
        string Description,
        string[] Tags,
        string[] Categories,
        string Owner
    );
}
