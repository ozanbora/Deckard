using Deckard.Core.Trade;

namespace Deckard.Core.Tests;

public class GoldTests
{
    [Theory]
    [InlineData("750k", 750_000L)]
    [InlineData("25M", 25_000_000L)]
    [InlineData("2.5b", 2_500_000_000L)]
    [InlineData("1,000,000", 1_000_000L)]
    [InlineData(" 40m gold ", 40_000_000L)]
    public void ParsesWhatPlayersWrite(string text, long gold)
    {
        Assert.Equal(gold, Gold.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("free")]
    [InlineData("-5m")]
    [InlineData("0")]
    public void RejectsNonAmounts(string text)
    {
        Assert.Null(Gold.Parse(text));
    }

    [Theory]
    [InlineData(950L, "950")]
    [InlineData(750_000L, "750k")]
    [InlineData(25_500_000L, "25.5M")]
    [InlineData(2_250_000_000L, "2.25B")]
    public void FormatsShort(long gold, string text)
    {
        Assert.Equal(text, Gold.Format(gold));
    }
}
