using EmojiService.Domain;
using EmojiService.Infrastructure;
using FluentAssertions;
using Xunit;

namespace EmojiService.Tests;

public class ManifestGeneratorTests
{
    private static readonly DateTime Now = new(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Emoji MakeEmoji(
        string uid,
        string primaryAlias,
        string state,
        string displayName = "Test",
        string contentType = "image/png",
        string assetRef = "assets/x.png",
        string? owner = null,
        string[]? tags = null,
        string[]? categories = null
    )
    {
        return Emoji
            .Create(
                EmojiUid.Create(uid).Value!,
                Alias.Create(primaryAlias).Value!,
                displayName,
                "A test emoji",
                contentType,
                assetRef,
                Now,
                Now,
                tags: tags,
                categories: categories,
                owner: owner,
                state: LifecycleState.Create(state).Value
            )
            .Value!;
    }

    private static IEmojiIndex BuildIndex(params Emoji[] emojis)
    {
        var index = new InMemoryEmojiIndex();
        foreach (var e in emojis)
            index.Upsert(e);
        return index;
    }

    private static Consumer MakeConsumer(
        string id = "01consumer",
        ConsumerSubsetFilter? filter = null
    )
    {
        return Consumer
            .Create(id, "Test Consumer", "local-fs", "/tmp/sync", Now, Now, subsetFilter: filter)
            .Value!;
    }

    [Fact]
    public void Generate_NullFilter_IncludesAllResolvableEmoji()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "active-1", "active"),
            MakeEmoji("01j00000000000000000000002", "dep-1", "deprecated"),
            MakeEmoji("01j00000000000000000000003", "pending-1", "pending"),
            MakeEmoji("01j00000000000000000000004", "disabled-1", "disabled")
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: null);

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(2);
        manifest
            .Emoji.Select(e => e.Uid)
            .Should()
            .BeEquivalentTo(["01j00000000000000000000001", "01j00000000000000000000002"]);
        manifest.Filter.Should().BeNull();
        manifest.ManifestVersion.Should().Be(ManifestGenerator.CurrentManifestVersion);
        manifest.ConsumerId.Should().Be("01consumer");
    }

    [Fact]
    public void Generate_StateFilter_OnlyIncludesMatchingState()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "active-1", "active"),
            MakeEmoji("01j00000000000000000000002", "dep-1", "deprecated")
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: new ConsumerSubsetFilter { State = "deprecated" });

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(1);
        manifest.Emoji[0].PrimaryAlias.Should().Be("dep-1");
    }

    [Fact]
    public void Generate_TagFilter_IntersectsTags()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active", tags: ["party", "fun"]),
            MakeEmoji("01j00000000000000000000002", "bb", "active", tags: ["work"]),
            MakeEmoji("01j00000000000000000000003", "cc", "active", tags: ["party"])
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: new ConsumerSubsetFilter { Tags = ["party"] });

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(2);
        manifest
            .Emoji.Select(e => e.Uid)
            .Should()
            .BeEquivalentTo(["01j00000000000000000000001", "01j00000000000000000000003"]);
    }

    [Fact]
    public void Generate_CategoryFilter_IntersectsCategories()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active", categories: ["approvals"]),
            MakeEmoji("01j00000000000000000000002", "bb", "active", categories: ["fun"])
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(
            filter: new ConsumerSubsetFilter { Categories = ["approvals"] }
        );

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(1);
        manifest.Emoji[0].PrimaryAlias.Should().Be("aa");
    }

    [Fact]
    public void Generate_OwnerFilter_ExactMatch()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active", owner: "eng"),
            MakeEmoji("01j00000000000000000000002", "bb", "active", owner: "design")
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: new ConsumerSubsetFilter { Owner = "eng" });

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(1);
        manifest.Emoji[0].Owner.Should().Be("eng");
    }

    [Fact]
    public void Generate_AliasPrefixFilter_MatchesPrimaryOrSecondary()
    {
        var e1 = Emoji
            .Create(
                EmojiUid.Create("01j00000000000000000000001").Value!,
                Alias.Create("eng-approved").Value!,
                "Approved",
                "desc",
                "image/png",
                "assets/x.png",
                Now,
                Now,
                secondaryAliases: ["eng-ok"]
            )
            .Value!;

        var e2 = Emoji
            .Create(
                EmojiUid.Create("01j00000000000000000000002").Value!,
                Alias.Create("design-wip").Value!,
                "WIP",
                "desc",
                "image/png",
                "assets/x.png",
                Now,
                Now
            )
            .Value!;

        var index = BuildIndex(e1, e2);
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: new ConsumerSubsetFilter { AliasPrefix = "eng-" });

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(1);
        manifest.Emoji[0].Uid.Should().Be("01j00000000000000000000001");
    }

    [Fact]
    public void Generate_CombinedFilters_AllMustMatch()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active", owner: "eng", tags: ["party"]),
            MakeEmoji("01j00000000000000000000002", "bb", "active", owner: "eng", tags: ["work"]),
            MakeEmoji(
                "01j00000000000000000000003",
                "cc",
                "active",
                owner: "design",
                tags: ["party"]
            )
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(
            filter: new ConsumerSubsetFilter { Owner = "eng", Tags = ["party"] }
        );

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().HaveCount(1);
        manifest.Emoji[0].Uid.Should().Be("01j00000000000000000000001");
    }

    [Fact]
    public void Generate_NoMatches_ReturnsEmptyManifest()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active", tags: ["fun"])
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer(filter: new ConsumerSubsetFilter { Tags = ["nonexistent"] });

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().BeEmpty();
        manifest.Filter.Should().NotBeNull();
    }

    [Fact]
    public void Generate_EmptyIndex_ReturnsEmptyManifest()
    {
        var index = BuildIndex();
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer();

        var manifest = generator.Generate(consumer);

        manifest.Emoji.Should().BeEmpty();
    }

    [Fact]
    public void Generate_FieldSelection_AllFieldsPopulated()
    {
        var emoji = MakeEmoji(
            "01j00000000000000000000001",
            "party",
            "active",
            displayName: "Party Parrot",
            contentType: "image/gif",
            assetRef: "assets/party.gif",
            owner: "eng",
            tags: ["fun", "party"],
            categories: ["reactions"]
        );

        var index = BuildIndex(emoji);
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer();

        var manifest = generator.Generate(consumer);
        var entry = manifest.Emoji.Should().ContainSingle().Subject;

        entry.Uid.Should().Be("01j00000000000000000000001");
        entry.PrimaryAlias.Should().Be("party");
        entry.SecondaryAliases.Should().BeEmpty();
        entry.DisplayName.Should().Be("Party Parrot");
        entry.Description.Should().Be("A test emoji");
        entry.ContentType.Should().Be("image/gif");
        entry.AssetReference.Should().Be("assets/party.gif");
        entry.Tags.Should().BeEquivalentTo(["fun", "party"]);
        entry.Categories.Should().BeEquivalentTo(["reactions"]);
        entry.Owner.Should().Be("eng");
        entry.State.Should().Be("active");
        entry.CreatedAt.Should().Be(Now);
        entry.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Generate_IsDeterministic_ForSameSnapshot()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000001", "aa", "active"),
            MakeEmoji("01j00000000000000000000002", "bb", "active")
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer();

        var m1 = generator.Generate(consumer);
        var m2 = generator.Generate(consumer);

        m1.Emoji.Select(e => e.Uid).Should().Equal(m2.Emoji.Select(e => e.Uid));
        m1.ManifestVersion.Should().Be(m2.ManifestVersion);
        m1.ConsumerId.Should().Be(m2.ConsumerId);
        m1.Filter.Should().BeNull();
        m2.Filter.Should().BeNull();
    }

    [Fact]
    public void Generate_EntriesSortedByUid()
    {
        var index = BuildIndex(
            MakeEmoji("01j00000000000000000000003", "cc", "active"),
            MakeEmoji("01j00000000000000000000001", "aa", "active"),
            MakeEmoji("01j00000000000000000000002", "bb", "active")
        );
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer();

        var manifest = generator.Generate(consumer);

        manifest
            .Emoji.Select(e => e.Uid)
            .Should()
            .Equal(
                "01j00000000000000000000001",
                "01j00000000000000000000002",
                "01j00000000000000000000003"
            );
    }

    [Fact]
    public void Generate_PreservesSecondaryAliasesInEntry()
    {
        var emoji = Emoji
            .Create(
                EmojiUid.Create("01j00000000000000000000001").Value!,
                Alias.Create("primary").Value!,
                "Test",
                "desc",
                "image/png",
                "assets/x.png",
                Now,
                Now,
                secondaryAliases: ["secondary-1", "secondary-2"]
            )
            .Value!;

        var index = BuildIndex(emoji);
        var generator = new ManifestGenerator(index);
        var consumer = MakeConsumer();

        var manifest = generator.Generate(consumer);
        var entry = manifest.Emoji.Should().ContainSingle().Subject;

        entry.PrimaryAlias.Should().Be("primary");
        entry.SecondaryAliases.Should().BeEquivalentTo(["secondary-1", "secondary-2"]);
    }
}
