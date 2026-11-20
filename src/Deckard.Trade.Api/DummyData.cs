using Deckard.Core.Items;
using Deckard.Core.Trade;

namespace Deckard.Trade.Api;

/// <summary>
/// Development only: fills the market with plausible listings built from the season's real item data (affixes that
/// roll on each item type, aspects, uniques), each checked by <see cref="ListingRules"/> like a real one. Rolls are
/// random, not the game's ranges, and the sellers are obviously fake ("Dummy0001#1000").
/// </summary>
public static class DummyData
{
    public static async Task<int> SeedAsync(TradeDb db, AffixCatalog catalog, ListingRules rules, int count, int seed = 15)
    {
        var random = new Random(seed);
        var affixes = catalog.Entries.Where(e => e.Kind == AffixKind.Affix && e.ItemTypes.Count > 0).ToList();
        var types = affixes.SelectMany(e => e.ItemTypes).Distinct().Order().ToList();
        var implicits = catalog.Entries.Where(e => e.Kind == AffixKind.Implicit).ToList();
        var tempers = catalog.Entries.Where(e => e.Kind == AffixKind.Tempered).ToList();
        var aspects = catalog.Entries.Where(e => e.Kind == AffixKind.Aspect).ToList();
        var uniques = catalog.Entries.Where(e => e.Kind == AffixKind.Unique && e.ItemType is not null).ToList();

        var sellers = Enumerable.Range(1, 40).Select(i => new UserRow { Id = Guid.NewGuid(), BattleTag = $"Dummy{i:D4}#{1000 + i}", Created = DateTimeOffset.UtcNow }).ToList();
        db.Users.AddRange(sellers);

        T Pick<T>(IReadOnlyList<T> list) => list[random.Next(list.Count)];
        double Roll() => Math.Round(1 + random.NextDouble() * 24, 1);

        var added = 0;
        for (var attempt = 0; added < count && attempt < count * 5; attempt++)
        {
            var type = Pick(types);
            var roll = random.NextDouble();
            var unique = roll < 0.15 ? uniques.Where(u => u.ItemType == type).ToList() is { Count: > 0 } fits ? Pick(fits) : null : null;
            var rarity = unique is not null ? "Unique" : roll < 0.4 ? "Rare" : "Legendary";
            var lines = new List<ListedAffix>();
            if (implicits.Where(i => i.ItemTypes.Contains(type)).ToList() is { Count: > 0 } typeImplicits)
                lines.Add(new ListedAffix(Pick(typeImplicits).Ids[0], Roll(), false, AffixSlot.Implicit));
            foreach (var affix in affixes.Where(a => a.ItemTypes.Contains(type)).OrderBy(_ => random.Next()).Take(rarity == "Rare" ? 4 : 3))
                lines.Add(new ListedAffix(affix.Ids[0], Roll(), random.NextDouble() < 0.12, AffixSlot.Affix));
            if (rarity != "Rare" && tempers.Count > 0)
                foreach (var temper in tempers.OrderBy(_ => random.Next()).Take(random.Next(3)))
                    lines.Add(new ListedAffix(temper.Ids[0], Roll(), false, AffixSlot.Tempered));
            var power = unique ?? (rarity == "Legendary" && aspects.Count > 0 ? Pick(aspects) : null);
            var name = unique?.Name ?? (power?.Name is { } aspect ? $"{aspect.Replace(" Aspect", "").Replace("Aspect of ", "")} {type}" : $"Rare {type}");
            var realm = random.NextDouble() < 0.6 ? Realm.Eternal(random.NextDouble() < 0.15) : Realm.Seasonal(catalog.Season, random.NextDouble() < 0.15);
            long? price = random.NextDouble() < 0.1 ? null : (long)Math.Round(Math.Exp(11.5 + random.NextDouble() * 11.5) / 1000) * 1000; // ~100k to ~10B gold, in thousands

            var listing = new NewListing(realm, type, name, rarity, 600 + random.Next(201), random.NextDouble() < 0.7, power?.Ids[0], lines, price);
            if (catalog.Goods.Count > 0 && random.NextDouble() < 0.12)
            {
                // About one in eight: runes, gems, elixirs and materials, by the stack.
                var goods = Pick(catalog.Goods);
                (type, name, rarity) = (goods.Kind, goods.Name, "Common");
                listing = new NewListing(realm, type, name, rarity, 0, false, null, [], price ?? 1_000_000, 1 + random.Next(20));
                (lines, price) = ([], listing.PriceGold);
            }
            if (rules.Check(listing).Count > 0)
                continue;
            var created = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(random.Next(60 * 24 * 10));
            db.Listings.Add(new ListingRow
            {
                Id = Guid.NewGuid(),
                SellerId = Pick(sellers).Id,
                Realm = realm.Kind,
                Hardcore = realm.Hardcore,
                Season = realm.Season,
                ItemType = type,
                Name = name,
                Rarity = rarity,
                ItemPower = listing.ItemPower,
                Ancestral = listing.Ancestral,
                AspectId = listing.AspectId,
                ClassMask = rules.ClassMask(listing),
                PriceGold = price,
                Quantity = listing.Quantity,
                Status = ListingStatus.Active,
                Created = created,
                Expires = created + ListingEndpoints.ListingLifetime,
                Affixes = [.. lines.Select((a, i) => new ListingAffixRow { Index = i, AffixId = a.Id, Value = a.Value, Greater = a.Greater, Slot = a.Slot })],
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }
}
