using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ContactDetailPatternsTests
{
    [Theory]
    [InlineData("Write to alice.example@acme.be about it")]
    [InlineData("carol+news@mail.example.org")]
    [InlineData("Call +32 470 12 34 56 tomorrow")]
    [InlineData("mobile 0470/12.34.56")]
    [InlineData("office 0032 2 123 45 67")]
    [InlineData("desk 02 123 45 67")]
    public void Contains_ContactDetail_True(string text)
    {
        // Assert
        ContactDetailPatterns.Contains(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("- [stated] 2026-10-04: met Carol")]
    [InlineData("run 01J8Y3N7Q2X9Z4A5B6C7D8E9F0 finished")]
    [InlineData("upgraded to v1.2.3 and 10.0.12")]
    [InlineData("business/areas/work-redis.md")]
    [InlineData("- [observed] 2026-10-03 [m365-mail 2026-10-03]: order 48201937 shipped")]
    [InlineData("the meeting is at 09:15")]
    public void Contains_NoContactDetail_False(string text)
    {
        // Assert
        ContactDetailPatterns.Contains(text).Should().BeFalse();
    }
}
