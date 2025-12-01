using FluentAssertions;
using Xunit;

namespace EmojiService.Tests;

public class PlaceholderTests
{
    [Fact]
    public void TrivialTest_ShouldPass()
    {
        true.Should().BeTrue();
    }
}
