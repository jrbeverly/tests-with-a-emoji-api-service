using EmojiService.Domain;
using FluentAssertions;
using Xunit;

namespace EmojiService.Tests;

public class EmojiTests
{
    private static readonly EmojiUid SampleUid = EmojiUid
        .Create("01jabc123xyz4567890abcdef")
        .Value!;
    private static readonly Alias SampleAlias = Alias.Create("party").Value!;
    private static readonly DateTime Now = new(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Party Parrot",
            "A festive parrot emoji",
            "image/gif",
            "assets/party.gif",
            Now,
            Now
        );

        result.IsSuccess.Should().BeTrue();
        result.Value!.Uid.Should().Be(SampleUid);
        result.Value.PrimaryAlias.Should().Be(SampleAlias);
        result.Value.DisplayName.Should().Be("Party Parrot");
        result.Value.Description.Should().Be("A festive parrot emoji");
        result.Value.ContentType.Should().Be("image/gif");
        result.Value.AssetReference.Should().Be("assets/party.gif");
        result.Value.CreatedAt.Should().Be(Now);
        result.Value.UpdatedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyDisplayName_ReturnsFailure(string? displayName)
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            displayName!,
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E300");
    }

    [Fact]
    public void Create_DisplayNameTooLong_ReturnsFailure()
    {
        var tooLong = new string('x', 201);

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            tooLong,
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E301");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyContentType_ReturnsFailure(string? contentType)
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "My Emoji",
            "desc",
            contentType!,
            "assets/x.png",
            Now,
            Now
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E302");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyAssetReference_ReturnsFailure(string? assetRef)
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "My Emoji",
            "desc",
            "image/png",
            assetRef!,
            Now,
            Now
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E303");
    }

    [Fact]
    public void Create_CreatedAfterUpdated_ReturnsFailure()
    {
        var created = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "My Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            created,
            updated
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E304");
    }

    [Fact]
    public void Create_TrimsDisplayNameAndDescription()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "  Party  ",
            "  A parrot  ",
            "image/gif",
            "assets/p.gif",
            Now,
            Now
        );

        result.Value!.DisplayName.Should().Be("Party");
        result.Value.Description.Should().Be("A parrot");
    }

    [Fact]
    public void Create_NullDescription_DefaultsToEmpty()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            null!,
            "image/png",
            "assets/x.png",
            Now,
            Now
        );

        result.Value!.Description.Should().Be("");
    }

    [Fact]
    public void Emoji_IsImmutable_PropertiesAreInitOnly()
    {
        var emoji = Emoji
            .Create(
                SampleUid,
                SampleAlias,
                "My Emoji",
                "desc",
                "image/png",
                "assets/x.png",
                Now,
                Now
            )
            .Value!;

        // Verify immutability — with expression creates a new instance
        var updated = emoji with
        {
            DisplayName = "New Name",
        };

        updated.DisplayName.Should().Be("New Name");
        emoji.DisplayName.Should().Be("My Emoji");
        updated.Uid.Should().Be(emoji.Uid);
    }

    // ── Tags validation ──

    [Fact]
    public void Create_TooManyTags_ReturnsFailure()
    {
        var tags = Enumerable.Repeat("tag", Emoji.MaxTags + 1).ToList();

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            tags: tags
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E305");
    }

    [Fact]
    public void Create_TagTooLong_ReturnsFailure()
    {
        var tags = new List<string> { new string('x', Emoji.MaxTagLength + 1) };

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            tags: tags
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E306");
    }

    [Theory]
    [InlineData("Bad Tag")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("special!char")]
    public void Create_TagInvalidFormat_ReturnsFailure(string tag)
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            tags: [tag]
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E307");
    }

    // ── Categories validation ──

    [Fact]
    public void Create_TooManyCategories_ReturnsFailure()
    {
        var categories = Enumerable.Repeat("cat", Emoji.MaxCategories + 1).ToList();

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            categories: categories
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E308");
    }

    [Fact]
    public void Create_CategoryTooLong_ReturnsFailure()
    {
        var categories = new List<string> { new string('x', Emoji.MaxCategoryLength + 1) };

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            categories: categories
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E309");
    }

    [Theory]
    [InlineData("Bad Cat")]
    [InlineData("-starts-hyphen")]
    public void Create_CategoryInvalidFormat_ReturnsFailure(string category)
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            categories: [category]
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E310");
    }

    // ── Owner validation ──

    [Fact]
    public void Create_OwnerTooLong_ReturnsFailure()
    {
        var tooLongOwner = new string('x', Emoji.MaxOwnerLength + 1);

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            owner: tooLongOwner
        );

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E311");
    }

    // ── Metadata: success and normalization ──

    [Fact]
    public void Create_WithValidMetadata_ReturnsSuccess()
    {
        var tags = new List<string> { "party", "festive", "dance" };
        var categories = new List<string> { "fun", "events" };

        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Party Parrot",
            "A festive parrot",
            "image/gif",
            "assets/p.gif",
            Now,
            Now,
            tags: tags,
            categories: categories,
            owner: "team-fun"
        );

        result.IsSuccess.Should().BeTrue();
        result.Value!.Tags.Should().BeEquivalentTo(["party", "festive", "dance"]);
        result.Value.Categories.Should().BeEquivalentTo(["fun", "events"]);
        result.Value.Owner.Should().Be("team-fun");
    }

    [Fact]
    public void Create_NormalizesTagsToLowercaseTrimmedDistinct()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            tags: ["  Party  ", "party", "PARTY", "  festive  "]
        );

        result.IsSuccess.Should().BeTrue();
        result.Value!.Tags.Should().BeEquivalentTo(["party", "festive"]);
    }

    [Fact]
    public void Create_NormalizesCategoriesToLowercaseTrimmedDistinct()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            categories: ["  Fun  ", "FUN", "  events  "]
        );

        result.IsSuccess.Should().BeTrue();
        result.Value!.Categories.Should().BeEquivalentTo(["fun", "events"]);
    }

    [Fact]
    public void Create_NullOwner_DefaultsToEmpty()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now,
            owner: null
        );

        result.Value!.Owner.Should().Be("");
    }

    [Fact]
    public void Create_EmptyTagsList_ReturnsEmptyTags()
    {
        var result = Emoji.Create(
            SampleUid,
            SampleAlias,
            "Emoji",
            "desc",
            "image/png",
            "assets/x.png",
            Now,
            Now
        );

        result.Value!.Tags.Should().BeEmpty();
        result.Value.Categories.Should().BeEmpty();
    }
}
