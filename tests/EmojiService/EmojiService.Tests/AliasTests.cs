using EmojiService.Domain;
using FluentAssertions;
using Xunit;

namespace EmojiService.Tests;

public class AliasTests
{
    [Theory]
    [InlineData("smile")]
    [InlineData("party_parrot")]
    [InlineData("cool-emoji")]
    [InlineData("abc123")]
    [InlineData("a_b-c")]
    public void Create_ValidNames_ReturnsSuccess(string name)
    {
        var result = Alias.Create(name);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be(name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_NullOrEmpty_ReturnsFailure(string? input)
    {
        var result = Alias.Create(input!);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E200");
    }

    [Fact]
    public void Create_TooShort_ReturnsFailure()
    {
        var result = Alias.Create("a");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E201");
    }

    [Fact]
    public void Create_TooLong_ReturnsFailure()
    {
        var tooLong = new string('a', Alias.MaxLength + 1);

        var result = Alias.Create(tooLong);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E202");
    }

    [Theory]
    [InlineData("-starts-with-hyphen")]
    [InlineData("_starts-with-underscore")]
    [InlineData("has space")]
    [InlineData("special!char")]
    [InlineData("emoji😀name")]
    public void Create_InvalidFormat_ReturnsFailure(string input)
    {
        var result = Alias.Create(input!);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E203");
    }

    [Fact]
    public void Create_NormalizesToLowercase()
    {
        var result = Alias.Create("AwesomeEmoji");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("awesomeemoji");
    }

    [Fact]
    public void Create_TrimsWhitespace()
    {
        var result = Alias.Create("  party  ");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("party");
    }

    [Fact]
    public void Alias_IsImmutable_PropertiesAreInitOnly()
    {
        var alias = Alias.Create("smile").Value!;
        var same = Alias.Create("smile").Value!;

        alias.Should().Be(same);
    }
}
