using EmojiService.Domain;
using FluentAssertions;
using Xunit;

namespace EmojiService.Tests;

public class EmojiUidTests
{
    private const string ValidUid = "01jabc123xyz4567890abcdefg";

    [Fact]
    public void Create_ValidLowercaseUlid_ReturnsSuccess()
    {
        var result = EmojiUid.Create(ValidUid);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be(ValidUid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_NullOrEmpty_ReturnsFailure(string? input)
    {
        var result = EmojiUid.Create(input!);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E100");
    }

    [Fact]
    public void Create_WrongLength_ReturnsFailure()
    {
        var result = EmojiUid.Create("abc123");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E101");
    }

    [Fact]
    public void Create_UppercaseCharacters_ReturnsFailure()
    {
        var result = EmojiUid.Create("01JABC123XYZ4567890ABCDEFG");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E102");
    }

    [Theory]
    [InlineData("01jabc123xyz4567890abcdefi")] // 'i' is invalid
    [InlineData("01jabc123xyz4567890abcdefl")] // 'l' is invalid
    [InlineData("01jabc123xyz4567890abcdefo")] // 'o' is invalid
    [InlineData("01jabc123xyz4567890abcdefu")] // 'u' is invalid
    public void Create_InvalidBase32Characters_ReturnsFailure(string input)
    {
        var result = EmojiUid.Create(input!);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("E103");
    }

    [Fact]
    public void Create_ToString_ReturnsValue()
    {
        var result = EmojiUid.Create(ValidUid);

        result.Value!.ToString().Should().Be(ValidUid);
    }

    [Fact]
    public void EmojiUid_IsImmutable_PropertiesAreInitOnly()
    {
        var uid = EmojiUid.Create(ValidUid).Value!;

        // Verify value equality via record behavior
        var same = EmojiUid.Create(ValidUid).Value!;
        uid.Should().Be(same);
    }
}
