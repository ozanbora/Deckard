using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>The trade database: users (by Battle.net account; BattleTags can change), listings, and each listing's affix lines, which search runs on.</summary>
public sealed class TradeDb(DbContextOptions<TradeDb> options) : DbContext(options)
{
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ListingRow> Listings => Set<ListingRow>();
    public DbSet<ListingAffixRow> ListingAffixes => Set<ListingAffixRow>();
    public DbSet<OfferRow> Offers => Set<OfferRow>();
    public DbSet<WantedRow> Wanted => Set<WantedRow>();
    public DbSet<BlockRow> Blocks => Set<BlockRow>();
    public DbSet<FlagRow> Flags => Set<FlagRow>();
    public DbSet<PatronRow> Patrons => Set<PatronRow>();
    public DbSet<TelemetryRow> Telemetry => Set<TelemetryRow>();
    public DbSet<TelemetryDailyRow> TelemetryDaily => Set<TelemetryDailyRow>();
    public DbSet<FeedbackRow> Feedback => Set<FeedbackRow>();
    public DbSet<FeedbackMuteRow> FeedbackMutes => Set<FeedbackMuteRow>();
    public DbSet<PendingSignInRow> PendingSignIns => Set<PendingSignInRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<UserRow>(user =>
        {
            user.ToTable("users");
            user.HasIndex(u => u.BattleTag).IsUnique();
            user.HasIndex(u => u.BattleNetId).IsUnique();
        });
        model.Entity<ListingRow>(listing =>
        {
            listing.ToTable("listings");
            listing.HasOne(l => l.Seller).WithMany().HasForeignKey(l => l.SellerId);
            listing.HasMany(l => l.Affixes).WithOne().HasForeignKey(a => a.ListingId).OnDelete(DeleteBehavior.Cascade);
            // Every search filters by realm and live status first.
            listing.HasIndex(l => new { l.Realm, l.Hardcore, l.Season, l.Status, l.ItemType, l.PriceGold });
            listing.HasIndex(l => l.SellerId);
        });
        model.Entity<ListingAffixRow>(affix =>
        {
            affix.ToTable("listing_affixes");
            affix.HasKey(a => new { a.ListingId, a.Index });
            // "This affix with at least this value" is the core search.
            affix.HasIndex(a => new { a.AffixId, a.Value });
        });
        model.Entity<OfferRow>(offer =>
        {
            offer.ToTable("offers");
            offer.HasOne(o => o.Listing).WithMany().HasForeignKey(o => o.ListingId).OnDelete(DeleteBehavior.Cascade);
            offer.HasOne(o => o.Buyer).WithMany().HasForeignKey(o => o.BuyerId);
            offer.HasIndex(o => new { o.ListingId, o.Status });
            offer.HasIndex(o => new { o.BuyerId, o.Updated });
            // PostgreSQL's row version (xmin): a change saved over one made since the row was read fails instead of
            // undoing it (e.g. the seller accepting while the buyer withdraws).
            offer.Property(o => o.Version).IsRowVersion();
        });
        model.Entity<WantedRow>(wanted =>
        {
            wanted.ToTable("wanted");
            wanted.HasOne(w => w.Buyer).WithMany().HasForeignKey(w => w.BuyerId);
            // Matching an item read in game looks up open buy orders of its realm and type, best paying first.
            wanted.HasIndex(w => new { w.Realm, w.Hardcore, w.Season, w.Status, w.ItemType, w.PriceGold });
            wanted.HasIndex(w => w.BuyerId);
        });
        model.Entity<BlockRow>(block =>
        {
            block.ToTable("blocks");
            block.HasKey(b => new { b.BlockerId, b.BlockedId });
            block.HasIndex(b => b.BlockedId); // "who blocked me", checked on every search
        });
        model.Entity<FlagRow>(flag =>
        {
            flag.ToTable("flags");
            flag.HasKey(f => f.UserId);
        });
        model.Entity<PatronRow>(patron =>
        {
            patron.ToTable("patrons");
            patron.HasKey(p => p.UserId);
            patron.HasIndex(p => p.PatreonUserId).IsUnique(); // one Deckard account per Patreon account
        });
        model.Entity<TelemetryRow>(row =>
        {
            row.ToTable("telemetry");
            row.HasIndex(t => t.At); // the 90-day cleanup
        });
        model.Entity<TelemetryDailyRow>(row =>
        {
            row.ToTable("telemetry_daily");
            row.HasKey(t => new { t.Day, t.Name, t.AppVersion });
        });
        model.Entity<FeedbackRow>(row =>
        {
            row.ToTable("feedback");
            row.HasIndex(f => new { f.UserId, f.Created }); // reports per player per day
        });
        model.Entity<FeedbackMuteRow>(row =>
        {
            row.ToTable("feedback_mutes");
            row.HasKey(m => m.UserId);
        });
        model.Entity<PendingSignInRow>(row =>
        {
            row.ToTable("pending_sign_ins");
            row.HasKey(p => p.Key);
            row.HasIndex(p => p.Expires); // the ones nobody finished are cleared by it
        });
        model.Entity<SessionRow>(row =>
        {
            row.ToTable("sessions");
            row.HasOne<UserRow>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            row.HasIndex(s => s.UserId);
            row.HasIndex(s => s.Expires); // the ones that ran out are cleared by it
        });
    }
}

public sealed class OfferRow
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public ListingRow? Listing { get; set; }
    public Guid BuyerId { get; set; }
    public UserRow? Buyer { get; set; }
    public long PriceGold { get; set; }
    public OfferStatus Status { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public long? CounterGold { get; set; }
    public uint Version { get; set; } // xmin, for optimistic concurrency

    // After the deal: each side marks it completed or reports it; these make up the players' trading records.
    public bool BuyerDone { get; set; }
    public bool SellerDone { get; set; }
    public string? BuyerReport { get; set; }
    public string? SellerReport { get; set; }
}

public sealed class UserRow
{
    public Guid Id { get; set; }
    public required string BattleTag { get; set; }
    /// <summary>The Battle.net account id; null for development sign-in users.</summary>
    public long? BattleNetId { get; set; }
    public DateTimeOffset Created { get; set; }
    public bool Banned { get; set; }

    // Presence, from the app's polling: shown to others only when ShowOnline, the zone only to accepted partners.
    public DateTimeOffset? LastSeen { get; set; }
    public bool InGame { get; set; }
    public bool ShowOnline { get; set; }
    public string? Zone { get; set; }
    public DateTimeOffset? ZoneAt { get; set; }
}

public enum ListingStatus : short
{
    Active,
    Sold,
    Withdrawn,
    Reserved, // the seller accepted an offer; out of search until sold or the deal is cancelled
}

public sealed class ListingRow
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public UserRow? Seller { get; set; }
    public RealmKind Realm { get; set; }
    public bool Hardcore { get; set; }
    public int Season { get; set; }
    public required string ItemType { get; set; }
    public required string Name { get; set; }
    public required string Rarity { get; set; }
    public int ItemPower { get; set; }
    public bool Ancestral { get; set; }
    public long? AspectId { get; set; }
    public int ClassMask { get; set; } // one bit per class (catalog order) that can use the item
    public long? PriceGold { get; set; }
    public ListingStatus Status { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public int Quantity { get; set; } = 1; // more than one only for goods (runes, gems, elixirs, materials)
    public long? SoldPriceGold { get; set; } // what it went for, for price checks
    public DateTimeOffset? SoldAt { get; set; }
    public List<ListingAffixRow> Affixes { get; set; } = [];
}

public sealed class ListingAffixRow
{
    public Guid ListingId { get; set; }
    public int Index { get; set; } // line order on the tooltip
    public long AffixId { get; set; }
    public double Value { get; set; }
    public bool Greater { get; set; }
    public AffixSlot Slot { get; set; }
}

/// <summary>A buy order. The search it describes is kept as JSON; matching runs in memory on the realm's open orders.</summary>
public sealed class WantedRow
{
    public Guid Id { get; set; }
    public Guid BuyerId { get; set; }
    public UserRow? Buyer { get; set; }
    public RealmKind Realm { get; set; }
    public bool Hardcore { get; set; }
    public int Season { get; set; }
    public string? ItemType { get; set; } // the search's item type, for the index; null when any type will do
    public required string Want { get; set; }
    public long PriceGold { get; set; }
    public WantedStatus Status { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public Guid? OfferId { get; set; } // the deal that filled it
}

/// <summary>A player hiding another: works both ways (neither sees the other's listings or buy orders, nor makes offers).</summary>
public sealed class BlockRow
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTimeOffset Created { get; set; }
}

/// <summary>A player waiting for a moderator's review (<see cref="Moderation"/>): off the market meanwhile.</summary>
public sealed class FlagRow
{
    public Guid UserId { get; set; }
    public DateTimeOffset Created { get; set; }
}

/// <summary>A player's linked Patreon account (<see cref="PatreonLink"/>); its own table, so linking adds no columns to users.</summary>
public sealed class PatronRow
{
    public Guid UserId { get; set; }
    public required string PatreonUserId { get; set; }
    public bool Patron { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public bool EarlyAccess { get; set; }   // a current tier is one of Trade:Patreon:EarlyAccessTiers
    public string? CreditName { get; set; } // shown in Deckard's credits while a patron; null: not shown
}

/// <summary>One usage event (<see cref="TelemetryEndpoints"/>): an installation's random id, never an account or IP address.</summary>
public sealed class TelemetryRow
{
    public long Id { get; set; }
    public Guid InstallId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset At { get; set; }
    public required string AppVersion { get; set; }
    public required string Windows { get; set; }
    public required string Channel { get; set; }
    public string? Props { get; set; } // the allowlisted values, as JSON
}

/// <summary>How often an event happened on a day, per app version: kept after the raw events are deleted.</summary>
public sealed class TelemetryDailyRow
{
    public DateOnly Day { get; set; }
    public required string Name { get; set; }
    public required string AppVersion { get; set; }
    public int Count { get; set; }
}

/// <summary>A bug report or feature request (<see cref="FeedbackEndpoints"/>); its screenshots are files, not rows.</summary>
public sealed class FeedbackRow
{
    public int Id { get; set; } // the report number players and issues see
    public Guid UserId { get; set; } // who sent it, for moderation only: never on GitHub
    public required string Kind { get; set; } // "bug" or "feature"
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Diagnostics { get; set; }
    public int Screenshots { get; set; } // kept on the server; 0 once deleted
    public DateTimeOffset Created { get; set; }
    public int? IssueNumber { get; set; } // null while waiting to be filed
    public string? IssueUrl { get; set; }
    public DateTimeOffset? IssueClosedAt { get; set; }
}

/// <summary>A player a moderator stopped from sending feedback (<c>--nofeedback</c>).</summary>
public sealed class FeedbackMuteRow
{
    public Guid UserId { get; set; }
}

/// <summary>
/// A sign-in or Patreon link in progress (<see cref="PendingSignIns"/>): the state waiting for the provider's callback, or
/// the one-time code waiting for the app. Minutes long; nothing about the player but their user id once signed in.
/// </summary>
public sealed class PendingSignInRow
{
    public required string Key { get; set; }
    public required string Payload { get; set; } // the step's record, as JSON
    public DateTimeOffset Expires { get; set; }
}

/// <summary>A signed-in session (<see cref="Sessions"/>): when it started and runs out, and whether the development sign-in made it.</summary>
public sealed class SessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool DevLogin { get; set; }
}
