using System.Text.Json;
using Deckard.Core.Items;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Development only (<c>dotnet run -- --demo &lt;BattleTag&gt;</c>): fills one signed-in player's market with something of
/// every kind, on the eternal realm, so each part of the Trade pages shows: their listings with pending offers, a
/// counter-offer, a reserved deal and one about to expire; their own offers in every state; deals with traders who are
/// in game and share their zone; trading records with reports; buy orders, theirs and others'; and 30 days of sales for
/// the price history. The traders are obviously fake ("Demo…"). Running it again adds another set.
/// </summary>
public static class DemoData
{
    private static readonly string[] Types = ["Ring", "Amulet", "Gloves", "Boots", "Helm"];
    private static readonly string[] Zones = ["Kyovashad", "Ked Bardu", "Gea Kul", "Zarbinzet", "Kurast Docks"];

    public static async Task<string> SeedAsync(TradeDb db, AffixCatalog catalog, ListingRules rules, string battleTag)
    {
        var me = await db.Users.SingleOrDefaultAsync(u => u.BattleTag == battleTag)
            ?? throw new InvalidOperationException($"No player {battleTag}: sign in to Deckard once first.");
        var random = new Random(27);
        var now = DateTimeOffset.UtcNow;
        var realm = Realm.Eternal();
        var run = random.Next(1000, 9999);

        // Traders: online and in game, some sharing their zone (presence far ahead of now, so it doesn't lapse while
        // the demo is looked at).
        var traders = Enumerable.Range(1, 8).Select(i => new UserRow
        {
            Id = Guid.NewGuid(), BattleTag = $"Demo{run}x{i}#{2000 + i}", Created = now - TimeSpan.FromDays(60),
            ShowOnline = i <= 5, InGame = i <= 5, LastSeen = now + TimeSpan.FromDays(1),
            Zone = i <= 3 ? Zones[i] : null, ZoneAt = i <= 3 ? now + TimeSpan.FromDays(1) : null,
        }).ToList();
        db.Users.AddRange(traders);
        UserRow Trader() => traders[random.Next(traders.Count)];

        var affixes = catalog.Entries.Where(e => e.Kind == AffixKind.Affix && e.ItemTypes.Count > 0).ToList();
        var aspects = catalog.Entries.Where(e => e.Kind == AffixKind.Aspect && e.Classes.Count == 0).ToList();
        var implicits = catalog.Entries.Where(e => e.Kind == AffixKind.Implicit).ToList();

        // A legendary of the type with a common aspect, its implicit and three affixes; a fixed first affix per type, so
        // buy orders for it and price checks find a market.
        NewListing Item(string type, long? price, double quality = 1)
        {
            for (var attempt = 0; ; attempt++)
            {
                var lines = new List<ListedAffix>();
                if (implicits.FirstOrDefault(i => i.ItemTypes.Contains(type)) is { } implicitEntry)
                    lines.Add(new ListedAffix(implicitEntry.Ids[0], Math.Round(10 + random.NextDouble() * 10, 1), false, AffixSlot.Implicit));
                var fits = affixes.Where(a => a.ItemTypes.Contains(type)).ToList();
                foreach (var affix in fits.Take(1).Concat(fits.Skip(1).OrderBy(_ => random.Next()).Take(2)))
                    lines.Add(new ListedAffix(affix.Ids[0], Math.Round((4 + random.NextDouble() * 16) * quality, 1), random.NextDouble() < 0.25, AffixSlot.Affix));
                var aspect = aspects[random.Next(aspects.Count)];
                var name = $"{aspect.Name?.Replace("Aspect of the ", "").Replace("Aspect of ", "").Replace(" Aspect", "")} {type}";
                var item = new NewListing(realm, type, name, "Legendary", 800, true, aspect.Ids[0], lines, price);
                if (rules.Check(item).Count == 0 || attempt > 20)
                    return item;
            }
        }

        ListingRow List(NewListing item, UserRow seller, DateTimeOffset created, ListingStatus status = ListingStatus.Active)
        {
            var row = new ListingRow
            {
                Id = Guid.NewGuid(), SellerId = seller.Id, Realm = item.Realm.Kind, Hardcore = item.Realm.Hardcore, Season = item.Realm.Season,
                ItemType = item.ItemType, Name = item.Name, Rarity = item.Rarity, ItemPower = item.ItemPower, Ancestral = item.Ancestral,
                AspectId = item.AspectId, ClassMask = rules.ClassMask(item), PriceGold = item.PriceGold, Quantity = item.Quantity,
                Status = status, Created = created, Expires = created + ListingEndpoints.ListingLifetime,
                Affixes = [.. item.Affixes.Select((a, i) => new ListingAffixRow { Index = i, AffixId = a.Id, Value = a.Value, Greater = a.Greater, Slot = a.Slot })],
            };
            db.Listings.Add(row);
            return row;
        }

        OfferRow Offer(ListingRow listing, UserRow buyer, long price, OfferStatus status, DateTimeOffset when)
        {
            var row = new OfferRow { Id = Guid.NewGuid(), ListingId = listing.Id, BuyerId = buyer.Id, PriceGold = price, Status = status, Created = when, Updated = when };
            db.Offers.Add(row);
            return row;
        }

        // 30 days of market: each day a few sales between traders (accepted offers, both sides marking them completed:
        // the traders' records) and new listings, around a price per type that drifts; now and then a report.
        var basePrice = new Dictionary<string, double> { ["Ring"] = 40e6, ["Amulet"] = 120e6, ["Gloves"] = 25e6, ["Boots"] = 15e6, ["Helm"] = 30e6 };
        var sales = 0;
        for (var day = 29; day >= 0; day--)
        {
            foreach (var type in Types)
            {
                var price = basePrice[type] * (1 + 0.35 * Math.Sin((30 - day) / 6.0 + type.Length));
                for (var n = random.Next(0, 3); n > 0; n--)
                {
                    var when = now - TimeSpan.FromDays(day) - TimeSpan.FromHours(random.Next(1, 20));
                    var seller = Trader();
                    var buyer = traders.First(t => t != seller);
                    var asking = (long)(price * (1.05 + random.NextDouble() * 0.3) / 100_000) * 100_000;
                    var sold = List(Item(type, asking), seller, when - TimeSpan.FromDays(1), ListingStatus.Sold);
                    sold.SoldAt = when;
                    sold.SoldPriceGold = (long)(price * (0.85 + random.NextDouble() * 0.3) / 100_000) * 100_000;
                    var deal = Offer(sold, buyer, sold.SoldPriceGold.Value, OfferStatus.Accepted, when);
                    (deal.BuyerDone, deal.SellerDone) = (true, random.NextDouble() < 0.9);
                    if (random.NextDouble() < 0.06)
                        deal.BuyerReport = "Took a long time to show up in game.";
                    sales++;
                }
                if (day < 13) // still live: listings last 14 days
                    for (var n = random.Next(1, 3); n > 0; n--)
                        List(Item(type, (long)(price * (0.9 + random.NextDouble() * 0.6) / 100_000) * 100_000), Trader(), now - TimeSpan.FromDays(day) - TimeSpan.FromHours(random.Next(1, 20)));
            }
        }

        // The player's listings.
        var ring = List(Item("Ring", 45_000_000, 1.2), me, now - TimeSpan.FromDays(2));
        Offer(ring, traders[0], 45_000_000, OfferStatus.Pending, now - TimeSpan.FromHours(3));
        Offer(ring, traders[1], 38_000_000, OfferStatus.Pending, now - TimeSpan.FromHours(1));
        var amulet = List(Item("Amulet", 150_000_000, 1.2), me, now - TimeSpan.FromDays(3));
        var countered = Offer(amulet, traders[2], 110_000_000, OfferStatus.Countered, now - TimeSpan.FromHours(5));
        countered.CounterGold = 135_000_000;
        var gloves = List(Item("Gloves", 30_000_000), me, now - TimeSpan.FromDays(4), ListingStatus.Reserved);
        Offer(gloves, traders[1], 28_000_000, OfferStatus.Accepted, now - TimeSpan.FromMinutes(40)); // the buyer is in game and shares their zone
        List(Item("Boots", 18_000_000), me, now - ListingEndpoints.ListingLifetime + TimeSpan.FromHours(20)); // expires in 20 h
        var helm = List(Item("Helm", null), me, now - TimeSpan.FromHours(6)); // best offer
        Offer(helm, traders[4], 22_000_000, OfferStatus.Pending, now - TimeSpan.FromMinutes(25));
        if (catalog.Goods.FirstOrDefault(g => g.Kind == "Rune") is { } rune)
            List(new NewListing(realm, rune.Kind, rune.Name, ListingRules.GoodsRarity, 0, false, null, [], 5_000_000, 5), me, now - TimeSpan.FromDays(1));

        // The player's offers on traders' listings, in every state.
        var theirs = Types.Select((type, i) => List(Item(type, (long)(basePrice[type] * 1.1 / 100_000) * 100_000), traders[i], now - TimeSpan.FromDays(i + 1))).ToList();
        Offer(theirs[0], me, (long)(theirs[0].PriceGold!.Value * 0.85), OfferStatus.Pending, now - TimeSpan.FromHours(2));
        var counter = Offer(theirs[1], me, (long)(theirs[1].PriceGold!.Value * 0.7), OfferStatus.Countered, now - TimeSpan.FromHours(4));
        counter.CounterGold = (long)(theirs[1].PriceGold!.Value * 0.9);
        theirs[2].Status = ListingStatus.Reserved;
        Offer(theirs[2], me, theirs[2].PriceGold!.Value, OfferStatus.Accepted, now - TimeSpan.FromMinutes(15)); // the seller is in game and shares their zone
        Offer(theirs[3], me, (long)(theirs[3].PriceGold!.Value * 0.5), OfferStatus.Declined, now - TimeSpan.FromDays(1));
        theirs[4].Status = ListingStatus.Reserved;
        Offer(theirs[4], me, theirs[4].PriceGold!.Value, OfferStatus.Accepted, now - TimeSpan.FromDays(1)).BuyerDone = true;

        // Buy orders: traders' (for items like the player's, to sell to) and the player's own.
        WantedRow Want(UserRow buyer, string type, long price, WantedStatus status, double? minFirstAffix = null)
        {
            var first = affixes.First(a => a.ItemTypes.Contains(type));
            var search = new ListingSearch(realm, type, "Legendary", Affixes: minFirstAffix is { } min ? [new AffixFilter(first.Ids[0], min)] : []);
            var row = new WantedRow
            {
                Id = Guid.NewGuid(), BuyerId = buyer.Id, Realm = realm.Kind, ItemType = type, Want = JsonSerializer.Serialize(search),
                PriceGold = price, Status = status, Created = now - TimeSpan.FromHours(random.Next(1, 48)), Expires = now + TimeSpan.FromDays(10),
            };
            db.Wanted.Add(row);
            return row;
        }
        foreach (var (type, i) in Types.Select((t, i) => (t, i)))
            Want(traders[(i + 2) % traders.Count], type, (long)(basePrice[type] * 0.8 / 100_000) * 100_000, WantedStatus.Open, i % 2 == 0 ? 5 : null);
        Want(me, "Amulet", 90_000_000, WantedStatus.Open, 10);
        Want(me, "Ring", 30_000_000, WantedStatus.Filled);
        Want(me, "Boots", 12_000_000, WantedStatus.Cancelled);

        await db.SaveChangesAsync();
        return $"Demo market for {battleTag}: {traders.Count} traders, {sales} sales over 30 days, 6 listings and 5 offers of yours, 8 buy orders (eternal realm, softcore).";
    }
}
