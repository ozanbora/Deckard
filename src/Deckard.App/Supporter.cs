namespace Deckard.App;

/// <summary>
/// The player's Patreon support, as the trade server last said, and the conveniences it lifts (ROADMAP.md). Never
/// anything in the market: alerts, listings, offers and search work the same for everyone. Checked in the app only,
/// which is fine for conveniences.
/// </summary>
public static class Supporter
{
    public const int FreeQuickSearches = 5, PatronQuickSearches = 25, FreeBuilds = 3;

    /// <summary>A Patreon account is linked to this Deckard account.</summary>
    public static bool Linked { get; private set; }

    /// <summary>The linked Patreon account is an active patron of Deckard's page.</summary>
    public static bool IsPatron { get; private set; }

    public static int QuickSearchLimit => IsPatron ? PatronQuickSearches : FreeQuickSearches;
    public static int LibraryLimit => IsPatron ? int.MaxValue : FreeBuilds;

    /// <summary>Stash sorting against every build in the library; free accounts sort against the shown build.</summary>
    public static bool SortsAllBuilds => IsPatron;

    /// <summary>A patron on a tier with early access (Champion): updates come from the early channel. The server decides.</summary>
    public static bool EarlyAccess { get; private set; }

    /// <summary>The name this patron shows in Deckard's credits, or null.</summary>
    public static string? CreditName { get; private set; }

    /// <summary>Raised on the UI thread when the support changes.</summary>
    public static event Action? Changed;

    /// <summary>Asks the trade server; signed out, or a server without Patreon, means free.</summary>
    public static async Task RefreshAsync()
    {
        var status = new Deckard.Core.Trade.PatreonStatus(false, false);
        if (TradeClient.Shared.SignedIn)
        {
            try
            {
                status = await TradeClient.Shared.PatreonStatusAsync();
            }
            catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
            {
                // Unreachable or no Patreon there: free limits until it answers.
            }
        }
        if ((status.Linked, status.Patron, status.EarlyAccess, status.CreditName) == (Linked, IsPatron, EarlyAccess, CreditName))
            return;
        (Linked, IsPatron, EarlyAccess, CreditName) = (status.Linked, status.Patron, status.EarlyAccess, status.CreditName);
        Changed?.Invoke();
    }
}
