using System.Text;
using Deckard.Core.Items;
using Deckard.Core.Trade;

namespace Deckard.Trade.Tests;

internal static class Fixtures
{
    public const long CritChance = 1, AttackSpeed = 2, RingResistance = 3, TemperedCrit = 4, RapidAspect = 5, StarlessSkies = 6, SorcererAspect = 7;

    // A season-15 catalog trimmed to a few ring affixes and powers.
    public const string Json = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus",
          "classes": ["Sorcerer", "Necromancer", "Rogue"],
          "entries": [
            { "kind": "affix", "keys": ["CritChance"], "ids": [1], "lines": ["+#% Critical Strike Chance"], "itemTypes": ["Ring", "Gloves"] },
            { "kind": "affix", "keys": ["AttackSpeed"], "ids": [2], "lines": ["+#% Attack Speed"], "itemTypes": ["Ring"] },
            { "kind": "implicit", "keys": ["INHERENT_Resistance_Ring"], "ids": [3], "lines": ["+#% Resistance to All Elements"], "itemTypes": ["Ring"] },
            { "kind": "tempered", "keys": ["Tempered_Crit"], "ids": [4], "lines": ["+#% Critical Strike Chance"] },
            { "kind": "aspect", "keys": ["legendary_generic_043"], "ids": [5], "name": "Rapid Aspect", "lines": ["Basic Skills gain +#% Attack Speed."] },
            { "kind": "unique", "keys": ["Ring_Unique_Starless"], "ids": [6], "name": "Ring of Starless Skies", "itemType": "Ring", "lines": ["Spending resources reduces costs."] },
            { "kind": "aspect", "keys": ["legendary_sorc_001"], "ids": [7], "name": "Aspect of Control", "classes": ["Sorcerer"], "lines": ["You deal x#% more damage to Immobilized enemies."] }
          ],
          "itemTypeClasses": { "Ring": ["Sorcerer", "Necromancer", "Rogue"], "Gloves": ["Sorcerer", "Necromancer", "Rogue"], "Wand": ["Sorcerer", "Necromancer"] },
          "goods": [ { "key": "Rune_Ber", "id": 900, "name": "Ber", "kind": "Rune" }, { "key": "Gem_Ruby_01", "id": 901, "name": "Crude Ruby", "kind": "Gem" } ],
          "unresolvedKeys": []
        }
        """;

    public static AffixCatalog Catalog() => AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)));

    /// <summary>A legendary ring on the eternal realm with crit, attack speed and the Rapid aspect.</summary>
    public static NewListing Ring(double crit = 5.5, long? price = 25_000_000) => new(
        Realm.Eternal(), "Ring", "Rapid Ring of Precision", "Legendary", 800, true, RapidAspect,
        [
            new ListedAffix(RingResistance, 20, false, AffixSlot.Implicit),
            new ListedAffix(CritChance, crit, true, AffixSlot.Affix),
            new ListedAffix(AttackSpeed, 8, false, AffixSlot.Affix),
        ],
        price);
}
