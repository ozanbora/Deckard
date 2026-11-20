namespace Deckard.Core.Trade;

public enum RealmKind
{
    Seasonal,
    Eternal,
}

/// <summary>
/// Where an item can change hands: never between the seasonal and eternal realms, nor between softcore and hardcore.
/// <paramref name="Season"/> is the season number on the seasonal realm and 0 on the eternal one.
/// </summary>
public sealed record Realm(RealmKind Kind, bool Hardcore, int Season)
{
    public static Realm Eternal(bool hardcore = false) => new(RealmKind.Eternal, hardcore, 0);
    public static Realm Seasonal(int season, bool hardcore = false) => new(RealmKind.Seasonal, hardcore, season);
}

public enum AffixSlot
{
    Implicit,
    Affix,
    Tempered,
}

/// <summary>An affix line on a listed item: the catalog's affix id, the rolled value, and whether it is a greater affix.</summary>
public sealed record ListedAffix(long Id, double Value, bool Greater, AffixSlot Slot);

/// <summary>What Deckard sends to list an item; built from the tooltip it read. Prices are in gold only.</summary>
/// <param name="AspectId">The legendary aspect or unique power (catalog id), if any.</param>
/// <param name="PriceGold">Asking price in gold; null for "best offer".</param>
/// <param name="Quantity">How many: more than one only for goods (runes, gems, elixirs, materials), whose
/// <paramref name="ItemType"/> is their kind and <paramref name="Name"/> the goods name; the price is for them all.</param>
public sealed record NewListing(
    Realm Realm, string ItemType, string Name, string Rarity, int ItemPower, bool Ancestral,
    long? AspectId, IReadOnlyList<ListedAffix> Affixes, long? PriceGold, int Quantity = 1);

/// <param name="Seller">The seller's BattleTag: only on the player's own listings. Others learn it through an accepted
/// <see cref="Offer"/>, so a BattleTag isn't handed to everyone who searches.</param>
/// <param name="Reserved">The seller accepted an offer: out of search until sold or the deal is cancelled.</param>
/// <param name="SellerInGame">The seller shows as online and is in Diablo IV right now (only sellers who opt in).</param>
public sealed record Listing(
    Guid Id, string Seller, Realm Realm, string ItemType, string Name, string Rarity, int ItemPower, bool Ancestral,
    long? AspectId, IReadOnlyList<ListedAffix> Affixes, long? PriceGold, DateTimeOffset Created, DateTimeOffset Expires,
    bool Reserved = false, bool SellerInGame = false, int Quantity = 1);

public enum OfferStatus
{
    Pending,
    Accepted,
    Declined,
    Withdrawn,
    Cancelled,
    Countered, // the seller answered with a higher price; the buyer accepts it, revises the offer or withdraws
}

/// <summary>A buyer's offer for a listing: the asking price or less (any amount for a best-offer listing), in gold.</summary>
public sealed record NewOffer(long PriceGold);

/// <param name="Counterpart">The other player's BattleTag, once the offer is accepted: both learn each other's, to add
/// each other in game. Null before that.</param>
/// <param name="CounterGold">The seller's counter-offer, while <see cref="OfferStatus.Countered"/>.</param>
/// <param name="CounterpartRecord">The other player's trading record, shown before and after accepting.</param>
/// <param name="MyDone">The player marked this accepted deal as completed.</param>
/// <param name="CounterpartInGame">The other player shows as online and is in Diablo IV right now.</param>
/// <param name="CounterpartZone">Where the other player is in game, for an accepted deal, if they share it.</param>
public sealed record Offer(
    Guid Id, Listing Listing, long PriceGold, OfferStatus Status, DateTimeOffset Created, DateTimeOffset Updated, string? Counterpart,
    long? CounterGold = null, TraderRecord? CounterpartRecord = null, bool MyDone = false, bool CounterpartInGame = false,
    string? CounterpartZone = null);

/// <summary>A player's trading record: deals the other side marked completed, and reports against them.</summary>
public sealed record TraderRecord(int Trades, int Reports);

/// <summary>Marks a listing sold; the price it went for, if not the accepted offer's or the asking price.</summary>
public sealed record SoldRequest(long? PriceGold = null);

/// <summary>A report on a deal that went wrong, in the player's words.</summary>
public sealed record TradeReport(string Reason);

/// <summary>
/// What the app tells the server every poll: whether the player is in the game, whether to show them as online, and
/// the zone they're in when they share it with accepted trade partners.
/// </summary>
public sealed record Presence(bool InGame, bool ShowOnline, string? Zone = null, bool ShareZone = false);

/// <summary>Prices of items like the one searched for: asking prices now listed, and prices recently sold for.</summary>
public sealed record PriceCheck(int Listed, long? LowGold, long? MedianGold, long? HighGold, int Sold, long? SoldMedianGold);

/// <summary>One day of a price history: how many matching items were listed and sold, and their median prices.</summary>
public sealed record PricePoint(DateOnly Day, int Listed, long? ListedMedianGold, int Sold, long? SoldMedianGold);

/// <summary>Daily prices of items like the one searched for, oldest day first; days without any are left out.</summary>
public sealed record PriceHistory(IReadOnlyList<PricePoint> Days);

/// <param name="Received">Offers on the player's listings.</param>
/// <param name="Sent">The player's offers on other listings.</param>
public sealed record OfferBox(IReadOnlyList<Offer> Received, IReadOnlyList<Offer> Sent);

/// <summary>Wants an affix rolled between <paramref name="Min"/> and <paramref name="Max"/> (either optional), and a greater one if asked.</summary>
public sealed record AffixFilter(long Id, double? Min = null, double? Max = null, bool Greater = false);

/// <summary>Which sellers' listings a search shows, by their trading record.</summary>
public enum SellerTrust
{
    Anyone,
    NoReports, // no deal of theirs was ever reported
    Proven,    // at least 5 completed trades, and no more than one in ten reported
}

/// <summary>
/// Blocks the player behind a listing, an offer or a buy order (one of them), without learning who they are: from then
/// on neither sees the other's listings or buy orders, nor can make the other an offer.
/// </summary>
public sealed record BlockRequest(Guid? ListingId = null, Guid? OfferId = null, Guid? WantedId = null);

public enum ListingSort
{
    PriceLow,
    PriceHigh,
    Newest,
    ItemPower,
}

/// <param name="Class">Only items this class can use (its item types, and no other class's aspect, power or affix).</param>
/// <param name="MinGreaterAffixes">Only items with at least this many greater affixes.</param>
/// <param name="SellerInGame">Only listings whose seller is in the game right now.</param>
/// <param name="Name">Only items of this exact name, e.g. a rune or gem.</param>
/// <param name="Sellers">Only sellers with this good a trading record.</param>
public sealed record ListingSearch(
    Realm Realm, string? ItemType = null, string? Rarity = null, int? MinItemPower = null, long? MaxPriceGold = null,
    long? AspectId = null, IReadOnlyList<AffixFilter>? Affixes = null, int Page = 0, string? Class = null,
    long? MinPriceGold = null, int MinGreaterAffixes = 0, ListingSort Sort = ListingSort.PriceLow, bool SellerInGame = false,
    string? Name = null, SellerTrust Sellers = SellerTrust.Anyone);

public sealed record SearchPage(IReadOnlyList<Listing> Listings, int Page, bool HasMore);

/// <summary>
/// A buy order: the items a player wants, described as a search (item type, power, affix rolls), and the gold they pay
/// for one. A seller whose item fits sells it to them at that price, which makes an accepted deal at once.
/// </summary>
public sealed record NewWanted(ListingSearch Want, long PriceGold);

public enum WantedStatus
{
    Open,
    Filled, // a seller sold an item to it: the deal is in both players' offers
    Cancelled,
}

/// <param name="Mine">The player's own buy order.</param>
public sealed record Wanted(Guid Id, ListingSearch Want, long PriceGold, WantedStatus Status, DateTimeOffset Created, DateTimeOffset Expires, bool Mine);

public sealed record WantedPage(IReadOnlyList<Wanted> Wanted, int Page, bool HasMore);

/// <summary>Asks for a Patreon link: the app's loopback port and its own state, which the browser brings back.</summary>
public sealed record PatreonLinkRequest(int Port, string State);

/// <summary>Where the app sends the browser to link Patreon.</summary>
public sealed record PatreonLinkUrl(string Url);

/// <summary>Whether the player linked a Patreon account, and whether they are an active patron of Deckard's campaign.</summary>
/// <param name="EarlyAccess">Their tier gets early builds of the app.</param>
/// <param name="CreditName">The name they show in Deckard's credits, if they chose to.</param>
public sealed record PatreonStatus(bool Linked, bool Patron, bool EarlyAccess = false, string? CreditName = null);

/// <summary>A patron's choice to be named in Deckard's credits, and under what name (1-32 characters, no links).</summary>
public sealed record CreditRequest(bool Show, string? Name);

/// <summary>
/// One thing that happened in the app, for anonymous usage statistics (only with the player's consent): an event
/// name from the server's allowlist, when, and a few named values (others are dropped).
/// </summary>
public sealed record TelemetryEvent(string Name, DateTimeOffset At, IReadOnlyDictionary<string, string>? Props = null);

/// <summary>
/// Events the app sends together. <paramref name="InstallId"/> is a random id of this installation, never tied to the
/// player's account on the server.
/// </summary>
public sealed record TelemetryBatch(Guid InstallId, string AppVersion, string Windows, string Channel, IReadOnlyList<TelemetryEvent> Events);

/// <summary>A sent bug report or feature request: its number, and its GitHub issue once filed.</summary>
public sealed record FeedbackReceipt(int ReportId, string? IssueUrl);
