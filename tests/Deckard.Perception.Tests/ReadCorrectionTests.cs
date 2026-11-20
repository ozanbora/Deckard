using System.Text;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Ocr;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Tests;

public class ReadCorrectionTests
{
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [],
          "entries": [
            { "kind": "affix", "keys": ["Willpower"], "ids": [1], "lines": ["+# Willpower"] },
            { "kind": "affix", "keys": ["Life"], "ids": [2], "lines": ["+# Maximum Life"] },
            { "kind": "setBonus", "keys": ["DarkPact2"], "ids": [9], "name": "Dark Pact", "required": 2, "lines": ["Lucky Hit: Up to a #% chance to deal # Cold damage"] }
          ],
          "rareNameWords": { "prefix": ["Dire", "Commander's", "Havoc", "Prime"], "suffix": ["Torment", "Reaver", "Strike", "Arbiter"] },
          "items": { "Sword2H": { "name": "Claymore", "type": "Two-Handed Sword", "twoHanded": true } },
          "affixText": {
            "1": { "lines": ["+# Willpower"], "slots": [[0, 1, 0]], "magicType": 0, "ranges": [[[750, 103, 124]]] },
            "2": { "lines": ["+# Maximum Life"], "slots": [[0, 1, 0]], "magicType": 0, "ranges": [[[750, 811, 1110]]] }
          }
        }
        """;

    private static readonly AffixCatalog Data = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));

    private static ItemTooltip Read(string willpower, string type = "Rare Two-Handed Sword", string name = "DIRE TORMENT", int power = 797)
    {
        string[] ocr = [name, type, $"{power} Item Power", willpower, "+1,770 Maximum Life", "Requires Level 66"];
        var lines = ocr.Select((text, i) => new TextLine(text, new PixelRect(10, 20 + i * 30, 340, 24))).ToList();
        return new TooltipParser(Data).Parse(lines, new PixelRect(0, 0, 363, 718));
    }

    private static IReadOnlyList<ValueFlag> Flags(ItemTooltip item, int line) => ReadCheck.Check(item, item.Lines[line], Data);

    [Fact]
    public void AValueInsideTheTooltipsOwnRangeIsFine()
    {
        Assert.Empty(Flags(Read("+213 Willpower [206 - 248]"), 0));
    }

    [Fact]
    public void AValueOutsideTheTooltipsOwnRangeIsFlaggedWithThatRange()
    {
        var flag = Assert.Single(Flags(Read("+218 Willpower [226 - 248]"), 0));

        Assert.Equal((0, 226d, 248d, true), (flag.Value, flag.Low, flag.High, flag.Shown));
    }

    [Theory]
    [InlineData("+213 Willpower", false)]   // a two-handed weapon's doubled roll
    [InlineData("+21 Willpower", true)]     // a digit lost
    [InlineData("+2,137 Willpower", true)]  // a digit too many
    public void WithoutAShownRangeOnlyAValueFarFromTheSeasonsRangeIsFlagged(string line, bool flagged)
    {
        var flags = Flags(Read(line), 0);

        Assert.Equal(flagged, flags.Count == 1);
        if (flagged)
            Assert.Equal((206d, 248d, false), (flags[0].Low, flags[0].High, flags[0].Shown)); // doubled: two-handed
    }

    [Fact]
    public void ARangeThatLostItsEndStillBoundsTheValueFromBelow()
    {
        Assert.Empty(Flags(Read("+213 Willpower [206 -"), 0));
        var flag = Assert.Single(Flags(Read("+21 Willpower [206 -"), 0));
        Assert.True(flag.Shown && double.IsPositiveInfinity(flag.High));
    }

    [Fact]
    public void BelowTheSeasonsFirstItemPowerTierNothingIsFlagged()
    {
        Assert.Empty(Flags(Read("+18 Willpower", power: 174), 0));
    }

    [Fact]
    public void ConfirmingAFlaggedValueClearsTheFlagForGood()
    {
        var flagged = Read("+21 Willpower");
        Assert.Single(Flags(flagged, 0));

        var confirmed = ReadCorrections.Entered(flagged, 0, [21]);
        Assert.Empty(Flags(confirmed, 0));
        Assert.False(ReadCorrections.IsEdited(confirmed)); // the read value stands: nothing was corrected

        var kept = ReadCorrections.With(null, ReadCorrections.ItemKey(confirmed), ReadCorrections.Of(confirmed, DateTimeOffset.UnixEpoch));
        Assert.Empty(Flags(ReadCorrections.Apply(Read("+21 Willpower"), kept), 0));
    }

    [Theory]
    [InlineData("COMMANDERTS REAVER", "Commander's Reaver")]
    [InlineData("1' DIRE TORMENT", "Dire Torment")]
    [InlineData("HAV0C STRIKE", "Havoc Strike")]
    [InlineData("PRITTIE ARBITER", "Prime Arbiter")] // the header font's M read as "TTI" (3440x1440)
    [InlineData("PRINIE ARBITER", "Prime Arbiter")] // or as "NI"
    [InlineData("GRIM COIL", "Grim Coil")] // words the season doesn't list: as read
    public void RareNamesAreCorrectedAgainstTheSeasonsWords(string ocr, string name)
    {
        Assert.Equal(name, Read("+213 Willpower", name: ocr).Name);
    }

    private static ItemTooltip Parse(params string[] ocr) =>
        new TooltipParser(Data).Parse([.. ocr.Select((text, i) => new TextLine(text, new PixelRect(10, 20 + i * 30, 340, 24)))], new PixelRect(0, 0, 363, 718));

    [Fact]
    public void ASetCharmsLinesEndWhereItsSetBegins()
    {
        // A set charm at 3440x1440, as OCR read it: the set's name and members, "(1/2)", "(2) Set:" and the bonuses.
        var charm = Parse("FER OF DARK", "PACT", "Set Charm", "820 Item Power", "+213 Willpower", "Dark Pact Phoba of Dark Pact",
            "—• Fer of Dark Pact Dark Pact (1/2)", "(2) Set:", "• Lucky Hit: Up to a chance to deal", "1,500 Cold damage", "Requires Level 67");

        Assert.Equal(["+213 Willpower"], charm.Lines.Select(l => l.Text));
        Assert.Equal(67, charm.RequiredLevel);
    }

    [Fact]
    public void TheTooltipBesideAComparedOneIsTheWornItem()
    {
        // The worn charm's "EQUIPPED" banner sits above its located frame and isn't read; the hovered one lists what's lost.
        var worn = Parse("FER OF DARK PACT", "Set Charm", "820 Item Power", "+213 Willpower");
        var hovered = Parse("FER OF DARK PACT", "Set Charm", "260 Item Power", "+230 Willpower", "Properties lost when equipped:", "+7% Gold Drop Rate");

        var marked = TooltipReader.MarkWorn([worn, hovered]);

        Assert.True(marked[0].IsEquipped);
        Assert.False(marked[1].IsEquipped);
        Assert.Same(worn, TooltipReader.MarkWorn([worn])[0]); // alone: nothing to tell it by

        // A floor's streaks framed an empty box beside the comparison: it doesn't count as a third tooltip.
        var floor = Parse("Vary");
        var withFloor = TooltipReader.MarkWorn([floor, worn, hovered]);
        Assert.False(withFloor[0].IsEquipped);
        Assert.True(withFloor[1].IsEquipped);
        Assert.False(withFloor[2].IsEquipped);
    }

    [Fact]
    public void ACorrectionComesBackOnTheSameMisreadOnly()
    {
        var misread = Read("+21 Willpower");
        var edited = ReadCorrections.WithValues(misread, 0, [213]);
        Assert.Equal(213, edited.Lines[0].Matches[0].Values[0]);
        Assert.Equal([21d], edited.Lines[0].ReadValues);
        Assert.Empty(Flags(edited, 0));

        var kept = ReadCorrections.With(null, ReadCorrections.ItemKey(edited), ReadCorrections.Of(edited, DateTimeOffset.UnixEpoch));

        Assert.Equal(213, ReadCorrections.Apply(Read("+21 Willpower"), kept).Lines[0].Matches[0].Values[0]);
        var otherRoll = ReadCorrections.Apply(Read("+230 Willpower"), kept); // another roll of the item: left as read
        Assert.Equal(230, otherRoll.Lines[0].Matches[0].Values[0]);
        Assert.False(ReadCorrections.IsEdited(otherRoll));
    }

    [Fact]
    public void RevertingLeavesNothingToKeep()
    {
        var edited = ReadCorrections.WithValues(Read("+21 Willpower"), 0, [213]);
        var reverted = ReadCorrections.Reverted(edited, 0);

        Assert.Equal(21, reverted.Lines[0].Matches[0].Values[0]);
        Assert.Null(ReadCorrections.Of(reverted, DateTimeOffset.UnixEpoch));
        Assert.Empty(ReadCorrections.With(
            ReadCorrections.With(null, ReadCorrections.ItemKey(edited), ReadCorrections.Of(edited, DateTimeOffset.UnixEpoch)),
            ReadCorrections.ItemKey(reverted), null));
    }

    [Theory]
    [InlineData("213", 213d)]
    [InlineData("1,770", 1770d)]
    [InlineData("+10.5%", 10.5)]
    [InlineData("10,5", 10.5)]
    [InlineData("x14 %", 14d)]
    [InlineData("2l3", null)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    public void ReadsTypedValues(string typed, double? value)
    {
        Assert.Equal(value, ReadCorrections.ParseValue(typed));
    }

    [Fact]
    public void ACorrectedItemPowerKeepsTheItemsKey()
    {
        var read = Read("+213 Willpower");
        var edited = ReadCorrections.WithItemPower(read, 800);

        Assert.Equal(ReadCorrections.ItemKey(read), ReadCorrections.ItemKey(edited));
        var kept = ReadCorrections.With(null, ReadCorrections.ItemKey(edited), ReadCorrections.Of(edited, DateTimeOffset.UnixEpoch));
        Assert.Equal(800, ReadCorrections.Apply(read, kept).ItemPower);
        Assert.False(ReadCorrections.WithItemPower(edited, 797).ItemPowerEdited);
    }
}
