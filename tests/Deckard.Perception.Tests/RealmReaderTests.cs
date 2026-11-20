using Deckard.Core.Trade;
using Deckard.Perception.Realms;

namespace Deckard.Perception.Tests;

public class RealmReaderTests
{
    [Theory]
    [InlineData("Seasonal | Lord of Hatred", RealmKind.Seasonal, false)] // read at 3440x1440
    [InlineData("Seasonal Hardcore | Lord of Hatred", RealmKind.Seasonal, true)]
    [InlineData("Eternal | Lord of Hatred", RealmKind.Eternal, false)]
    [InlineData("Hardcore | Lord of Hatred", RealmKind.Eternal, true)] // an eternal hardcore character shows "Hardcore" alone
    public void ReadsTheRealmLine(string text, RealmKind kind, bool hardcore)
    {
        var realm = RealmReader.Parse(text, season: 15);

        Assert.NotNull(realm);
        Assert.Equal(kind, realm.Kind);
        Assert.Equal(hardcore, realm.Hardcore);
        Assert.Equal(kind == RealmKind.Seasonal ? 15 : 0, realm.Season);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,046 2,172")] // the game's world: damage numbers
    [InlineData("Seasonal Eternal")] // two characters' lines, not one
    public void NothingElse(string text) => Assert.Null(RealmReader.Parse(text, season: 15));

    [Fact]
    public void Region_IsTheCentredBandUnderTheName()
    {
        var r = RealmReader.Region(3440, 1440);

        Assert.InRange(1190, r.Y, r.Bottom); // the realm line, measured
        Assert.InRange(1720, r.X, r.Right); // centred
        Assert.True(r.X > 700, "the character list on the left stays out");
    }
}
