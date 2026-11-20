using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using Deckard.Core.Items;
using Deckard.Core.Trade;
using Deckard.Perception.Tooltips;
using Deckard.Perception.Trade;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Deckard.App;

/// <summary>
/// The Trade tab: search the market for items by type and affix rolls, list the last item Deckard read, and trade by
/// offers. Listings are priced in gold and only match players up: a buyer offers the asking price or less, and only an
/// accepted offer shares the two BattleTags, so the players can meet and trade by hand in game. Offers are polled
/// every <see cref="PollInterval"/>; new ones and acceptances are raised as <see cref="Notice"/>s.
/// </summary>
public sealed partial class TradeView : UserControl
{
    private readonly TradeClient _client = TradeClient.Shared;
    private AffixCatalog? _catalog;
    private ItemTooltip? _lastItem;

    private readonly ComboBox _realm = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>The realm picker, shown by the main window in its rail under Trade.</summary>
    public ComboBox RealmBox => _realm;
    private UIElement[] _pageViews = [];
    private int _shown; // the page the rail shows: TradePage's order
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };

    private readonly ComboBox _class = new() { Header = "Class", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _itemType = new() { Header = "Item type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _rarity = new() { Header = "Rarity", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _minPower = new() { Header = "Min item power", PlaceholderText = "e.g. 750" };
    private readonly TextBox _minPrice = new() { PlaceholderText = "min" };
    private readonly TextBox _maxPrice = new() { PlaceholderText = "max, e.g. 50M" };
    private readonly ComboBox _minGreater = new() { Header = "Greater affixes", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _sellerInGame = new() { Content = "Sellers in game only" };
    private readonly ComboBox _sellers = new() { Header = "Sellers", Items = { "Anyone", "Without reports", "Proven: 5+ trades, few reports" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoSuggestBox _aspect = new() { Header = "Aspect or unique power", PlaceholderText = "Click or type to pick a power" };
    private readonly ComboBox _sort = new() { MinWidth = 190 };
    private readonly StackPanel _affixRows = new() { Spacing = 6 };
    private readonly Button _addAffix = new() { Content = "+ Add affix" };
    private readonly List<AffixRow> _affixFilters = [];
    private readonly Dictionary<AutoSuggestBox, AffixEntry> _chosenAffixes = [];

    /// <summary>The server reads at most this many affix filters (ListingRules.MaxAffixLines).</summary>
    private const int MaxAffixFilters = 8;

    private sealed record AffixRow(AutoSuggestBox Affix, TextBox Min, TextBox Max, CheckBox Greater, Grid Panel);
    private readonly ObservableCollection<Listing> _listings = [];
    private readonly ItemsRepeater _results = new() { Layout = new MasonryLayout() };
    private readonly Button _more = new() { Content = "More results", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly PriceHistoryChart _priceHistory = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 8), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

    private readonly StackPanel _sellPreview = new() { Spacing = 8 };
    private readonly TextBox _price = new() { Header = "Price (gold)", PlaceholderText = "e.g. 25M; empty for best offer", Width = 260 };
    private readonly Button _list = new() { Content = "List it", IsEnabled = false, VerticalAlignment = VerticalAlignment.Bottom }; // level with the price box, not its header
    private readonly TextBlock _similar = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.85 };
    private readonly TextBlock _priceWarning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Visibility = Visibility.Collapsed };
    private PriceCheck? _similarPrices;

    /// <summary>Items queued from the item card over the game, to price and list at once (kept while Deckard runs).</summary>
    private sealed record QueuedItem(NewListing Draft, TextBox Price);
    private readonly List<QueuedItem> _queue = [];
    private readonly StackPanel _queuePanel = new() { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
    private const int MaxQueued = 20; // the server takes 30 new listings an hour
    private string? _similarFor; // the item the price check above is for
    private readonly ObservableCollection<Listing> _myListings = [];
    private readonly ItemsRepeater _mine = new() { Layout = new MasonryLayout() };
    private readonly TextBlock _noListings = new() { Text = "You have no active listings.", Opacity = 0.7, Visibility = Visibility.Collapsed };
    private readonly TextBox _mineFilter = new() { PlaceholderText = "Filter by name", Width = 200 };
    private readonly ComboBox _mineShow = new() { Items = { "All", "With offers", "Open", "Reserved" }, SelectedIndex = 0, MinWidth = 140 };
    private readonly ComboBox _mineSort = new() { Items = { "Newest", "Price: high to low", "Price: low to high", "Expiring soon", "Most offers" }, SelectedIndex = 0, MinWidth = 170 };
    private IReadOnlyList<Listing> _allMine = [];

    // Quick searches: one per item of the loaded build, and the player's own saved filters (kept per account).
    /// <param name="Watched">Deckard checks it every few minutes and shows a notice for each new listing that matches.</param>
    private sealed record SavedSearch(string Name, ListingSearch Search, bool Watched = false);
    private static string? SavedSearchesFile => TradeClient.Shared.AccountFolder is { } folder ? Path.Combine(folder, "quick-searches.json") : null;
    private List<SavedSearch> _saved = LoadSavedSearches();
    private readonly MenuFlyout _buildSearches = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private readonly MenuFlyout _savedSearches = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private Dictionary<Guid, int> _waitingOffers = []; // pending offers per listing of the player's, from the offer polling
    // Offers and buy orders: two columns each, every list a virtualizing masonry of cards like the results.
    private readonly ObservableCollection<Offer> _received = [], _sent = [];
    private readonly ObservableCollection<Wanted> _myOrders = [], _otherOrders = [];
    private readonly TextBlock _noReceived = Empty("No offers yet."), _noSent = Empty("You haven't made any offers."),
        _noMyOrders = Empty("None yet. Set filters on the Search page, then \"Post buy order\"."), _noOtherOrders = Empty("No one is buying that right now.");
    private readonly TextBlock _buyingTitle = Subtitle("Players buying");
    private string _offersShown = ""; // what the Offers page shows, so a poll that changed nothing doesn't redraw it
    /// <summary>Offers waiting for the player's answer, after each poll: the rail shows the count.</summary>
    public event Action<int>? WaitingOffersChanged;
    private int _waiting = -1;
    private readonly Dictionary<Guid, OfferStatus> _seenOffers = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _poll;
    private bool _polling;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>A new offer on the player's listing, or the player's offer accepted; raised on the UI thread.</summary>
    public event Action<TradeNotice>? Notice;

    private ListingSearch? _search;

    /// <summary>Builds a listing's card when the masonry layout scrolls it into view; cards fill their column.</summary>
    /// <param name="mine">The player's own listings, with the seller's buttons instead of "Make offer".</param>
    private sealed partial class CardFactory(TradeView view, bool mine) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var card = view.Card((Listing)args.Data, mine);
            card.MaxWidth = double.PositiveInfinity;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            return card;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) { } // cards are cheap; a scrolled-away one is just dropped
    }

    /// <summary>Builds any card (an offer, a buy order) as the masonry layout scrolls it into view; cards fill their column.</summary>
    private sealed partial class Cards(Func<object, Border> build) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var card = build(args.Data);
            card.MaxWidth = double.PositiveInfinity;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            return card;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    private static TextBlock Subtitle(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };

    private static TextBlock Empty(string text) => new() { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    /// <summary>Two columns, each a title, a note shown when its list is empty, and the list as cards; each scrolls on its own.</summary>
    private static Grid TwoColumns((TextBlock Title, TextBlock Empty, ItemsRepeater Cards) left, (TextBlock Title, TextBlock Empty, ItemsRepeater Cards) right)
    {
        static ScrollViewer Column((TextBlock Title, TextBlock Empty, ItemsRepeater Cards) column, Thickness margin) => new()
        {
            Content = new StackPanel { Spacing = 8, Margin = margin, Children = { column.Title, column.Empty, column.Cards } },
        };
        var second = Column(right, new Thickness(8, 8, 16, 16));
        Grid.SetColumn(second, 1);
        return new Grid { ColumnDefinitions = { new(), new() }, Children = { Column(left, new Thickness(16, 8, 8, 16)), second } };
    }

    private ItemsRepeater Repeater<T>(ObservableCollection<T> items, Func<T, Border> card) =>
        new() { Layout = new MasonryLayout(), ItemsSource = items, ItemTemplate = new Cards(o => card((T)o)) };

    /// <summary>Puts new items in a card list, and shows its empty note when there are none.</summary>
    private static void Fill<T>(ObservableCollection<T> list, IEnumerable<T> items, TextBlock empty)
    {
        list.Clear();
        foreach (var item in items)
            list.Add(item);
        empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public TradeView()
    {
        _results.ItemsSource = _listings;
        _results.ItemTemplate = new CardFactory(this, mine: false);
        _mine.ItemsSource = _myListings;
        _mine.ItemTemplate = new CardFactory(this, mine: true);
        // Short names: the picker sits in the main window's narrow rail.
        _realm.Items.Add("Eternal");
        _realm.Items.Add("Eternal HC");
        _realm.SelectedIndex = 0;
        _realm.SelectionChanged += async (_, _) =>
        {
            // Saved for the account, so the overlay card lists in it too and it comes back next time.
            if (_realm.SelectedIndex >= 0 && _realm.SelectedIndex != AppSettings.Shared.DefaultRealm)
            {
                _appliedRealm = _realm.SelectedIndex;
                AppSettings.Shared.DefaultRealm = _realm.SelectedIndex;
                AppSettings.Shared.NotifyChanged();
            }
            await SearchAsync(page: 0);
        };
        AppSettings.Shared.Changed += ApplyRealmSetting;
        // Signing out (title bar) or in as someone else: drop the last player's results and offers, then start over.
        _client.SessionChanged += async () =>
        {
            _listings.Clear();
            _myListings.Clear();
            _received.Clear();
            _sent.Clear();
            _myOrders.Clear();
            _otherOrders.Clear();
            _offersShown = "";
            _seenOffers.Clear();
            _saved = LoadSavedSearches(); // this account's own
            ShowSavedSearches();
            ShowSellPreview();
            if (_client.SignedIn && _catalog is not null)
                await Run(StartSessionAsync);
        };

        // The page's name; the pages and the realm are picked in the main window's rail.
        // Signing in is the app's start-up dialog.
        var header = new Grid { Margin = new Thickness(16, 12, 16, 4) };
        header.Children.Add(_title);

        var search = SearchPage();
        var mine = MinePage();
        var offers = TwoColumns(
            (Subtitle("Offers on your listings"), _noReceived, Repeater(_received, o => OfferCard(o, received: true))),
            (Subtitle("Your offers"), _noSent, Repeater(_sent, o => OfferCard(o, received: false))));
        var wanted = TwoColumns(
            (Subtitle("Your buy orders"), _noMyOrders, Repeater(_myOrders, WantedCard)),
            (_buyingTitle, _noOtherOrders, Repeater(_otherOrders, WantedCard)));
        _pageViews = [search, mine, offers, wanted];
        ShowPage(TradePage.Search);

        var layout = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
        layout.Children.Add(header);
        Grid.SetRow(_message, 2);
        _message.Margin = new Thickness(16, 4, 16, 4);
        layout.Children.Add(_message);
        Grid.SetRow(search, 3);
        Grid.SetRow(mine, 3);
        Grid.SetRow(offers, 3);
        Grid.SetRow(wanted, 3);
        layout.Children.Add(search);
        layout.Children.Add(mine);
        layout.Children.Add(offers);
        layout.Children.Add(wanted);
        Content = layout;
        ShowSellPreview();
    }

    /// <summary>The season's data: item types and affix names for the filters, and text for listed affixes.</summary>
    public void SetCatalog(AffixCatalog catalog)
    {
        _catalog = catalog;
        _realm.Items.Add($"Season {catalog.Season}");
        _realm.Items.Add($"Season {catalog.Season} HC");
        ApplyRealmSetting();
        _class.Items.Clear();
        _class.Items.Add("Any");
        foreach (var className in catalog.Classes)
            _class.Items.Add(className);
        _class.SelectedIndex = 0;
        RefreshItemTypes();
        if (_client.SignedIn) // the app signs in before it loads the season data
            _ = Run(StartSessionAsync);
    }

    private string? SelectedClass => _class.SelectedItem as string is { } c && c != "Any" ? c : null;
    private string? SelectedType => _itemType.SelectedItem as string is { } t && t != "Any" ? t : null;

    /// <summary>The item types the chosen class can equip (all of them for any class), keeping the choice if it still fits.</summary>
    private void RefreshItemTypes()
    {
        if (_catalog is null)
            return;
        var keep = SelectedType;
        var className = SelectedClass;
        _itemType.Items.Clear();
        _itemType.Items.Add("Any");
        foreach (var type in _catalog.Entries.Where(e => e.Kind == AffixKind.Affix).SelectMany(e => e.ItemTypes).Distinct().Order())
            if (className is null || !_catalog.ItemTypeClasses.TryGetValue(type, out var classes) || classes.Contains(className))
                _itemType.Items.Add(type);
        foreach (var kind in _catalog.GoodsKinds)
            _itemType.Items.Add(kind); // runes, gems, elixirs and materials: any class can use them
        _itemType.SelectedItem = keep is not null && _itemType.Items.Contains(keep) ? keep : "Any";
    }

    // Goods (runes, gems, elixirs, materials) are found by name, picked here when the item type is one of their kinds.
    private readonly AutoSuggestBox _goodsName = new() { Header = "Name", PlaceholderText = "Click or type to pick one", Visibility = Visibility.Collapsed };

    private bool IsGoodsKind(string? type) => type is not null && _catalog?.GoodsKinds.Contains(type) == true;

    /// <summary>The goods of the chosen kind whose names hold every typed word.</summary>
    private List<string> GoodsNames(string? kind, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. (_catalog?.Goods ?? []).Where(g => g.Kind == kind && words.All(w => g.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(g => g.Name)];
    }

    /// <summary>A name picker for goods of the kind <paramref name="kind"/> gives: clicking lists them all, typing narrows.</summary>
    private void WireGoodsPicker(AutoSuggestBox box, Func<string?> kind)
    {
        box.GotFocus += (_, e) =>
        {
            if ((e.OriginalSource as Control)?.FocusState != FocusState.Pointer)
                return; // only a click opens the list, as with the other pickers
            box.ItemsSource = GoodsNames(kind(), "");
            box.IsSuggestionListOpen = true;
        };
        box.TextChanged += (b, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            b.ItemsSource = GoodsNames(kind(), b.Text);
            b.IsSuggestionListOpen = true;
        };
        box.SuggestionChosen += (b, e) => b.Text = e.SelectedItem as string ?? b.Text;
    }

    /// <summary>The item Deckard read last, offered for listing on the My listings page.</summary>
    /// <summary>Opens My listings, where the last item read waits to be listed.</summary>
    public void ShowSell() => ShowPage(TradePage.Listings);

    private static readonly string[] PageTitles = ["Search", "My listings", "Offers", "Buy orders"];

    /// <summary>Shows one of the trade pages (picked in the main window's rail) and refreshes what it lists.</summary>
    public async void ShowPage(TradePage page)
    {
        _shown = (int)page;
        _title.Text = PageTitles[_shown];
        for (var i = 0; i < _pageViews.Length; i++)
            _pageViews[i].Visibility = i == _shown ? Visibility.Visible : Visibility.Collapsed;
        switch (page)
        {
            case TradePage.Listings:
                ShowSellPreview(); // and its price check, now that the page is open
                await RefreshMineAsync();
                break;
            case TradePage.Offers:
                await PollOffersAsync();
                break;
            case TradePage.BuyOrders:
                await RefreshWantedAsync();
                break;
        }
    }

    public void SetLastItem(ItemTooltip item)
    {
        _lastItem = item;
        ShowSellPreview();
    }

    private Realm SelectedRealm => _realm.SelectedIndex switch
    {
        1 => Realm.Eternal(hardcore: true),
        2 => Realm.Seasonal(_catalog?.Season ?? 0),
        3 => Realm.Seasonal(_catalog?.Season ?? 0, hardcore: true),
        _ => Realm.Eternal(),
    };

    private Grid SearchPage()
    {
        _rarity.Items.Add("Any");
        foreach (var rarity in new[] { "Legendary", "Unique", "Mythic", "Rare", "Magic" })
            _rarity.Items.Add(rarity);
        _rarity.SelectedIndex = 0;

        _minGreater.Items.Add("Any");
        for (var i = 1; i <= 4; i++)
            _minGreater.Items.Add($"At least {i}");
        _minGreater.SelectedIndex = 0;
        _sort.Items.Add("Price: low to high");
        _sort.Items.Add("Price: high to low");
        _sort.Items.Add("Newest first");
        _sort.Items.Add("Item power");
        _sort.Items.Add("Best fit for my build");
        _sort.SelectedIndex = 0; // in ListingSort's order, then build fit, which is sorted here
        _sort.SelectionChanged += async (_, _) => await SearchAsync(page: 0);

        _class.SelectionChanged += (_, _) => RefreshItemTypes();
        _itemType.SelectionChanged += (_, _) => DropUnfitAffixes();
        WirePicker(_aspect, PowerKinds);
        foreach (var box in new[] { _minPower, _minPrice, _maxPrice })
            box.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync(page: 0); };
        _addAffix.Click += (_, _) => AddAffixRow();
        AddAffixRow();

        // The less used filters wait under "More filters", so the column shows what most searches need.
        var price = new Grid { ColumnSpacing = 8, RowSpacing = 8, ColumnDefinitions = { new(), new() }, RowDefinitions = { new() { Height = GridLength.Auto }, new() } };
        var priceLabel = new TextBlock { Text = "Price (gold)" };
        Grid.SetColumnSpan(priceLabel, 2);
        price.Children.Add(priceLabel);
        Grid.SetRow(_minPrice, 1);
        price.Children.Add(_minPrice);
        Grid.SetRow(_maxPrice, 1);
        Grid.SetColumn(_maxPrice, 1);
        price.Children.Add(_maxPrice);
        WireGoodsPicker(_goodsName, () => SelectedType);
        _itemType.SelectionChanged += (_, _) =>
        {
            _goodsName.Visibility = IsGoodsKind(SelectedType) ? Visibility.Visible : Visibility.Collapsed;
            _goodsName.Text = "";
        };
        var more = new Expander
        {
            Header = "More filters",
            Content = new StackPanel { Spacing = 10, Children = { _class, _minPower, price, _minGreater, _sellers, _sellerInGame } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var go = new Button { Content = "Search", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch };
        go.Click += async (_, _) => await SearchAsync(page: 0);
        var clear = new Button { Content = "Clear", HorizontalAlignment = HorizontalAlignment.Stretch };
        clear.Click += async (_, _) => { ClearFilters(); await SearchAsync(page: 0); };
        var post = AmountButton("Post buy order", "You pay (gold)", null,
            "Sellers whose item fits these filters can sell it to you at this price; then you see each other's BattleTags.",
            price => Run(async () =>
            {
                await _client.CreateWantedAsync(new NewWanted(CurrentFilters(), price));
                _message.Text = $"Buy order posted for {Gold.Format(price)} gold: it's on the Buy orders page.";
            }));
        ToolTipService.SetToolTip(post, "Buy what these filters describe from whoever has it");

        _more.Click += async (_, _) => await SearchAsync(page: (_search?.Page ?? 0) + 1);

        // Left: the filters, in one column that scrolls on its own, with Search and Clear on top.
        var buttons = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(), new() } };
        buttons.Children.Add(go);
        Grid.SetColumn(clear, 1);
        buttons.Children.Add(clear);
        var filters = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16, 8, 16, 16),
            Children = { buttons, _itemType, _goodsName, _rarity, _aspect, new TextBlock { Text = "Affixes, rolled between min and max" }, _affixRows, _addAffix, more },
        };
        var left = new ScrollViewer { Content = filters, Width = FilterWidth };

        // Right: quick searches, buy order and sort above the price history and the results.
        _sort.MinWidth = 0;
        var tools = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(), new() { Width = GridLength.Auto } } };
        tools.Children.Add(QuickSearchBar());
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { post, _sort } };
        Grid.SetColumn(right, 1);
        tools.Children.Add(right);
        var results = new ScrollViewer { Content = new StackPanel { Spacing = 8, Margin = new Thickness(8, 8, 16, 16), Children = { tools, _priceHistory, _results, _more } } };
        Grid.SetColumn(results, 1);
        return new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() }, Children = { left, results } };
    }

    private const double FilterWidth = 340;

    /// <summary>Two columns: quick listing of the last item read on the left, the player's listings on the right.</summary>
    private Grid MinePage()
    {
        _list.Click += async (_, _) => await ListLastItemAsync();
        var sell = new StackPanel { Spacing = 8, Width = 360, Margin = new Thickness(16, 8, 8, 16) };
        sell.Children.Add(new TextBlock { Text = "Sell the last item read", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        sell.Children.Add(_sellPreview);
        sell.Children.Add(_similar);
        sell.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _price, _list } });
        sell.Children.Add(_priceWarning);
        _price.TextChanged += (_, _) => WarnAboutPrice();
        sell.Children.Add(new TextBlock { Text = "Players find you by your BattleTag and trade in game. Gold only: real-money trading breaks Blizzard's terms.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        sell.Children.Add(_queuePanel);
        ShowQueue();
        sell.Children.Add(GoodsForm());
        _mineFilter.TextChanged += (_, _) => ShowMine();
        _mineShow.SelectionChanged += (_, _) => ShowMine();
        _mineSort.SelectionChanged += (_, _) => ShowMine();
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _mineFilter, _mineShow, new TextBlock { Text = "Sort", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }, _mineSort },
        };
        var listings = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8, 8, 16, 16),
            Children = { new TextBlock { Text = "Your listings", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] }, tools, _noListings, _mine },
        };
        var left = new ScrollViewer { Content = sell };
        var right = new ScrollViewer { Content = listings };
        Grid.SetColumn(right, 1);
        ShowSellPreview();
        return new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() }, Children = { left, right } };
    }

    private static readonly AffixKind[] LineKinds = [AffixKind.Affix, AffixKind.Tempered, AffixKind.Implicit];
    private static readonly AffixKind[] PowerKinds = [AffixKind.Aspect, AffixKind.Unique, AffixKind.Mythic];

    /// <summary>An affix line by its text; an aspect or unique power by its name.</summary>
    private sealed record AffixChoice(AffixEntry Entry)
    {
        public override string ToString() => Entry.Kind switch
        {
            AffixKind.Tempered => Entry.Lines[0].Replace("#", "…") + "  (tempered)",
            AffixKind.Implicit => Entry.Lines[0].Replace("#", "…") + "  (implicit)",
            AffixKind.Aspect => Entry.Name ?? Entry.Lines[0],
            AffixKind.Unique => (Entry.Name ?? Entry.Lines[0]) + "  (unique)",
            AffixKind.Mythic => (Entry.Name ?? Entry.Lines[0]) + "  (mythic)",
            _ => Entry.Lines[0].Replace("#", "…"),
        };
    }

    /// <summary>Entries of the given kinds that fit the chosen item type and class, matching every typed word.</summary>
    private List<AffixChoice> AffixChoices(string text, AffixKind[] kinds)
    {
        if (_catalog is null)
            return [];
        var (type, className) = (SelectedType, SelectedClass);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. _catalog.Entries
            .Where(e => kinds.Contains(e.Kind)
                && (type is null || e.ItemTypes.Count == 0 || e.ItemTypes.Contains(type) || e.ItemType == type)
                && (className is null || e.Classes.Count == 0 || e.Classes.Contains(className)))
            .Select(e => new AffixChoice(e))
            .Where(c => words.All(w => c.ToString().Contains(w, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(c => c.ToString())
            .OrderBy(c => c.Entry.Kind is not (AffixKind.Affix or AffixKind.Aspect)).ThenBy(c => SortKey(c.ToString()), StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Sorts by the words: "+…% Critical Strike Chance" files under C, not under "+".</summary>
    private static string SortKey(string label) => label.TrimStart('+', '-', '…', '%', 'x', ' ', '#');

    /// <summary>Clicking lists every entry that fits the chosen item type and class; typing narrows the list.</summary>
    private void WirePicker(AutoSuggestBox box, AffixKind[] kinds)
    {
        // Only a click opens the list. Tab, or focus handed back when the other picker's list closes, must not:
        // two pickers opening on any focus passed it back and forth forever.
        box.GotFocus += (_, e) => { if ((e.OriginalSource as Control)?.FocusState == FocusState.Pointer) Suggest(box, kinds); };
        box.TextChanged += (b, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Suggest(b, kinds); };
        box.SuggestionChosen += (b, e) => { if (e.SelectedItem is AffixChoice choice) _chosenAffixes[b] = choice.Entry; };
        box.QuerySubmitted += (b, e) =>
        {
            // Enter without picking: take the entry if the text names exactly one.
            var choice = e.ChosenSuggestion as AffixChoice ?? (AffixChoices(b.Text, kinds) is [var only] ? only : null);
            if (choice is null)
                return;
            _chosenAffixes[b] = choice.Entry;
            b.Text = choice.ToString();
        };
    }

    private void Suggest(AutoSuggestBox box, AffixKind[] kinds)
    {
        // Showing the chosen entry's own label lists everything; edited text filters and drops the choice.
        var chosen = _chosenAffixes.TryGetValue(box, out var entry) && new AffixChoice(entry).ToString() == box.Text;
        if (!chosen)
            _chosenAffixes.Remove(box);
        box.ItemsSource = AffixChoices(chosen ? "" : box.Text, kinds);
        box.IsSuggestionListOpen = true;
    }

    private void AddAffixRow()
    {
        var affix = new AutoSuggestBox { PlaceholderText = "Click or type to pick an affix" };
        var min = new TextBox { PlaceholderText = "min", MinWidth = 0 };
        var max = new TextBox { PlaceholderText = "max", MinWidth = 0 };
        var greater = new CheckBox { Content = "★ Greater", MinWidth = 0 };
        var remove = new Button { Content = "✕", Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(greater, "Greater affix only");
        ToolTipService.SetToolTip(remove, "Remove this affix");
        WirePicker(affix, LineKinds);
        // The affix on one line, its rolls and buttons under it: the filter column is narrow.
        var line = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 4,
            ColumnDefinitions = { new(), new(), new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } },
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto } },
        };
        Grid.SetColumnSpan(affix, 4);
        line.Children.Add(affix);
        FrameworkElement[] parts = [min, max, greater, remove];
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetRow(parts[i], 1);
            Grid.SetColumn(parts[i], i);
            line.Children.Add(parts[i]);
        }
        var row = new AffixRow(affix, min, max, greater, line);
        remove.Click += (_, _) =>
        {
            _affixFilters.Remove(row);
            _chosenAffixes.Remove(affix);
            _affixRows.Children.Remove(row.Panel);
            if (_affixFilters.Count == 0)
                AddAffixRow();
            _addAffix.IsEnabled = true;
        };
        _affixFilters.Add(row);
        _affixRows.Children.Add(row.Panel);
        _addAffix.IsEnabled = _affixFilters.Count < MaxAffixFilters;
    }

    /// <summary>Clears picked affixes and powers that no longer fit after the item type changed.</summary>
    private void DropUnfitAffixes()
    {
        var fit = AffixChoices("", [.. LineKinds, .. PowerKinds]).Select(c => c.Entry).ToHashSet();
        foreach (var box in _affixFilters.Select(f => f.Affix).Append(_aspect))
            if (_chosenAffixes.TryGetValue(box, out var entry) && !fit.Contains(entry))
            {
                _chosenAffixes.Remove(box);
                box.Text = "";
            }
    }

    private void ClearFilters()
    {
        _class.SelectedIndex = _rarity.SelectedIndex = _minGreater.SelectedIndex = 0;
        _sellerInGame.IsChecked = false;
        _sellers.SelectedIndex = 0;
        _goodsName.Text = "";
        _goodsName.Visibility = Visibility.Collapsed;
        _itemType.SelectedItem = "Any";
        _minPower.Text = _minPrice.Text = _maxPrice.Text = _aspect.Text = "";
        _chosenAffixes.Clear();
        _affixFilters.Clear();
        _affixRows.Children.Clear();
        AddAffixRow();
    }

    private static double? Number(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Once signed in (at start, or as another player): search, catch up on offers, keep polling.</summary>
    private async Task StartSessionAsync()
    {
        ShowSellPreview();
        await SearchAsync(page: 0);
        _seenOffers.Clear();
        await PollOffersAsync(notify: false); // what was already there shows in the Offers tab, without pop-ups
        _poll ??= StartPolling();
    }

    /// <summary>
    /// Quick search: a menu of one-click searches for the loaded build's items, a menu of the player's saved filters
    /// (to search, watch or delete), and "Save filters", which names the filters' current choices.
    /// </summary>
    private StackPanel QuickSearchBar()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Crit rings", Width = 220 };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var full = new TextBlock { Opacity = 0.8, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Visibility = Visibility.Collapsed };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { name, save, full } } };
        flyout.Opening += (_, _) => full.Visibility = Visibility.Collapsed;
        save.Click += (_, _) =>
        {
            if (name.Text.Trim() is not { Length: > 0 } title)
                return;
            // A supporter convenience (ROADMAP.md): how many searches are kept, never what they find or how often
            // watched ones are checked. Saving under a name already there replaces it, so that always works; saves
            // over the cap (from a lapsed pledge) are kept, only new ones wait.
            var replaces = _saved.Any(s => s.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (!replaces && _saved.Count >= Supporter.QuickSearchLimit)
            {
                full.Text = Supporter.IsPatron
                    ? $"You keep {Supporter.PatronQuickSearches} quick searches. Delete one (Saved menu) to save this."
                    : $"Free accounts keep {Supporter.FreeQuickSearches} quick searches, Patreon supporters {Supporter.PatronQuickSearches} (Settings, Patreon). Delete one (Saved menu) to save this.";
                full.Visibility = Visibility.Visible;
                return;
            }
            _saved.RemoveAll(s => s.Name.Equals(title, StringComparison.OrdinalIgnoreCase)); // same name: replace it
            _saved.Add(new SavedSearch(title, CurrentFilters()));
            StoreSavedSearches();
            ShowSavedSearches();
            name.Text = "";
            flyout.Hide();
        };
        ShowSavedSearches();
        SetBuild(null); // the hint, until a build loads
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new DropDownButton { Content = "Your build", Flyout = _buildSearches },
                new DropDownButton { Content = "Saved", Flyout = _savedSearches },
                new Button { Content = "Save filters", Flyout = flyout },
            },
        };
    }

    private static MenuFlyoutItem Hint(string text) => new() { Text = text, IsEnabled = false };

    /// <summary>
    /// One quick search per item of the build: its item type, the build's class, its unique power or aspect, and up to
    /// three of its planned affixes (more would rarely match anything).
    /// </summary>
    public void SetBuild(Deckard.Core.Builds.BuildVariant? variant)
    {
        _variant = variant;
        RedrawResults(); // their build fit changed
        _buildSearches.Items.Clear();
        if (variant is null || _catalog is not { } catalog)
        {
            _buildSearches.Items.Add(Hint("Load a build to search for its items."));
            return;
        }
        var className = variant.ClassIndex < catalog.Classes.Count ? catalog.Classes[variant.ClassIndex] : null;
        foreach (var (slot, item) in variant.Items.OrderBy(i => i.Key))
        {
            if (slot >= 20 || !catalog.Items.TryGetValue(item.ItemKey, out var info))
                continue; // seals and charms aren't gear to trade
            var power = info.Unique
                ? catalog.Entries.FirstOrDefault(e => e.Kind is AffixKind.Unique or AffixKind.Mythic && e.Name == info.Name)
                : item.Aspects.Select(a => catalog.FindById(a.Id)).FirstOrDefault(e => e is not null);
            var affixes = info.Unique ? [] : item.Affixes.Select(a => catalog.FindById(a.Id))
                .OfType<AffixEntry>().Where(e => e.Kind == AffixKind.Affix).Take(3).Select(e => new AffixFilter(e.Ids[0])).ToList();
            // A unique without a power of its own (runewords like Insight are only fixed affixes) is found by its name.
            var search = new ListingSearch(SelectedRealm, info.Type, AspectId: power?.Ids[0], Affixes: affixes, Class: className,
                Name: info.Unique && power is null ? info.Name : null);
            var entry = new MenuFlyoutItem { Text = $"{info.Type} · {(info.Unique ? info.Name : power?.Name ?? "affixes")}" };
            ToolTipService.SetToolTip(entry, string.Join("\n", affixes.Select(a => catalog.FindById(a.Id)?.Lines[0].Replace("#", "…")).Prepend(power?.Name ?? (info.Unique ? $"Named {info.Name}" : null)).OfType<string>()));
            entry.Click += async (_, _) => await ApplyFilters(search);
            _buildSearches.Items.Add(entry);
        }
        if (_buildSearches.Items.Count == 0)
            _buildSearches.Items.Add(Hint("Load a build to search for its items."));
    }

    /// <summary>
    /// The Saved menu: each saved search (● when watched) runs on a click; Watch and Delete hold one entry per search.
    /// </summary>
    private void ShowSavedSearches()
    {
        _savedSearches.Items.Clear();
        var watching = new MenuFlyoutSubItem { Text = "Watch for new listings" };
        var deleting = new MenuFlyoutSubItem { Text = "Delete" };
        foreach (var saved in _saved)
        {
            var entry = new MenuFlyoutItem { Text = saved.Watched ? "● " + saved.Name : saved.Name };
            if (saved.Watched)
                ToolTipService.SetToolTip(entry, "Watched: Deckard tells you about new listings that match.");
            entry.Click += async (_, _) => await ApplyFilters(saved.Search);
            _savedSearches.Items.Add(entry);
            var watch = new ToggleMenuFlyoutItem { Text = saved.Name, IsChecked = saved.Watched };
            watch.Click += (_, _) =>
            {
                if (!saved.Watched && _saved.Count(s => s.Watched) >= MaxWatched)
                {
                    _message.Text = $"You can watch {MaxWatched} searches at once; stop watching one first.";
                    ShowSavedSearches(); // undo the toggle's own check
                    return;
                }
                _saved[_saved.IndexOf(saved)] = saved with { Watched = !saved.Watched };
                _watchedSince.Remove(saved.Name); // a newly watched search starts from now
                StoreSavedSearches();
                ShowSavedSearches();
            };
            watching.Items.Add(watch);
            var delete = new MenuFlyoutItem { Text = saved.Name };
            delete.Click += (_, _) =>
            {
                _saved.Remove(saved);
                StoreSavedSearches();
                ShowSavedSearches();
            };
            deleting.Items.Add(delete);
        }
        if (_saved.Count == 0)
        {
            _savedSearches.Items.Add(Hint("Set filters, then \"Save filters\" to keep them here."));
            return;
        }
        _savedSearches.Items.Add(new MenuFlyoutSeparator());
        _savedSearches.Items.Add(watching);
        _savedSearches.Items.Add(deleting);
    }

    private static List<SavedSearch> LoadSavedSearches()
    {
        try
        {
            return SavedSearchesFile is { } file && File.Exists(file)
                ? System.Text.Json.JsonSerializer.Deserialize<List<SavedSearch>>(File.ReadAllText(file)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return []; // an unreadable file starts the list over rather than breaking the Trade tab
        }
    }

    private void StoreSavedSearches()
    {
        if (SavedSearchesFile is not { } file)
            return; // signed out: nothing to keep them for
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(_saved));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _message.Text = "Couldn't save your quick searches: " + e.Message;
        }
    }

    /// <summary>
    /// Switches to the account's saved realm (the realm list is in <see cref="AppSettings.Realms"/>' order), only
    /// when that setting changed, e.g. when another account signs in.
    /// </summary>
    private void ApplyRealmSetting()
    {
        var wanted = AppSettings.Shared.DefaultRealm;
        if (wanted == _appliedRealm || wanted >= _realm.Items.Count)
            return;
        _appliedRealm = wanted;
        _realm.SelectedIndex = wanted;
    }

    private int _appliedRealm = 0; // the realm list starts on Eternal

    /// <summary>The filter panel's choices as a search: what Search sends and what a Quick Search saves.</summary>
    private ListingSearch CurrentFilters()
    {
        var rarity = _rarity.SelectedItem as string is { } r && r != "Any" ? r : null;
        var affixes = _affixFilters.Where(f => _chosenAffixes.ContainsKey(f.Affix))
            .Select(f => new AffixFilter(_chosenAffixes[f.Affix].Ids[0], Number(f.Min), Number(f.Max), f.Greater.IsChecked == true))
            .ToList();
        return new ListingSearch(SelectedRealm, SelectedType, rarity,
            int.TryParse(_minPower.Text, out var power) ? power : null, Gold.Parse(_maxPrice.Text),
            _chosenAffixes.TryGetValue(_aspect, out var aspect) ? aspect.Ids[0] : null, affixes, Class: SelectedClass,
            MinPriceGold: Gold.Parse(_minPrice.Text), MinGreaterAffixes: _minGreater.SelectedIndex, Sort: _sort.SelectedIndex is > 0 and < BuildFitSort ? (ListingSort)_sort.SelectedIndex : ListingSort.PriceLow,
            SellerInGame: _sellerInGame.IsChecked == true, Sellers: (SellerTrust)Math.Max(0, _sellers.SelectedIndex),
            // Goods by a name from their list; other items by an exact name a quick search set (e.g. a runeword).
            Name: IsGoodsKind(SelectedType)
                ? (GoodsNames(SelectedType, "").Contains(_goodsName.Text) ? _goodsName.Text : null)
                : _goodsName.Text.Trim() is { Length: > 0 } name ? name : null);
    }

    /// <summary>Searches the market for a rune, gem or other goods by name, e.g. from the build sheet's runes and gems.</summary>
    public Task FindGoodsAsync(string kind, string name) => ApplyFilters(new ListingSearch(SelectedRealm, kind, Name: name));

    /// <summary>Puts a search's filters into the panel and runs it. The realm stays the one chosen at the top.</summary>
    private async Task ApplyFilters(ListingSearch search)
    {
        _class.SelectedItem = search.Class is { } c && _class.Items.Contains(c) ? c : "Any";   // also narrows the item types
        _itemType.SelectedItem = search.ItemType is { } t && _itemType.Items.Contains(t) ? t : "Any";
        _rarity.SelectedItem = search.Rarity is { } r && _rarity.Items.Contains(r) ? r : "Any";
        _minPower.Text = search.MinItemPower?.ToString(CultureInfo.InvariantCulture) ?? "";
        _minPrice.Text = search.MinPriceGold is { } min ? Gold.Format(min) : "";
        _maxPrice.Text = search.MaxPriceGold is { } max ? Gold.Format(max) : "";
        _minGreater.SelectedIndex = Math.Clamp(search.MinGreaterAffixes, 0, _minGreater.Items.Count - 1);
        _sellerInGame.IsChecked = search.SellerInGame;
        _sellers.SelectedIndex = (int)search.Sellers;
        _goodsName.Text = search.Name ?? "";
        if (search.Name is not null)
            _goodsName.Visibility = Visibility.Visible; // shown, so the name the search uses can be seen and cleared
        _chosenAffixes.Clear();
        Choose(_aspect, search.AspectId);
        _affixFilters.Clear();
        _affixRows.Children.Clear();
        foreach (var filter in (search.Affixes ?? []).Take(MaxAffixFilters))
        {
            AddAffixRow();
            var row = _affixFilters[^1];
            Choose(row.Affix, filter.Id);
            row.Min.Text = filter.Min?.ToString(CultureInfo.InvariantCulture) ?? "";
            row.Max.Text = filter.Max?.ToString(CultureInfo.InvariantCulture) ?? "";
            row.Greater.IsChecked = filter.Greater;
        }
        if (_affixFilters.Count == 0)
            AddAffixRow();
        var sort = (int)search.Sort;
        if (sort != _sort.SelectedIndex && sort < _sort.Items.Count)
            _sort.SelectedIndex = sort; // searches through its SelectionChanged
        else
            await SearchAsync(page: 0);
    }

    /// <summary>Picks a catalog entry in a picker box by id, as if chosen from its list; clears the box when unknown.</summary>
    private void Choose(AutoSuggestBox box, long? id)
    {
        if (id is { } known && _catalog?.FindById(known) is { } entry)
        {
            _chosenAffixes[box] = entry;
            box.Text = new AffixChoice(entry).ToString();
        }
        else
        {
            _chosenAffixes.Remove(box);
            box.Text = "";
        }
    }

    private async Task SearchAsync(int page)
    {
        if (!_client.SignedIn)
            return;
        if (page == 0)
            _search = CurrentFilters();
        if (_search is null)
            return;
        var search = _search with { Page = page };
        await Run(async () =>
        {
            var results = await _client.SearchAsync(search);
            _search = search;
            if (page == 0)
                _listings.Clear();
            // shortcut: build fit sorts each page of 50 on its own, not the whole market; sort on the server when it knows builds
            var shown = _sort.SelectedIndex == BuildFitSort ? results.Listings.OrderByDescending(l => Fit(l)?.Score ?? -1) : results.Listings.AsEnumerable();
            foreach (var listing in shown)
                _listings.Add(listing);
            _more.Visibility = results.HasMore ? Visibility.Visible : Visibility.Collapsed;
            _message.Text = page == 0 && results.Listings.Count == 0 ? "No listings match." : "";
            if (page == 0)
                await ShowPriceHistoryAsync(search);
        });
    }

    /// <summary>The searched item's prices over the last 30 days, above the results; only for a search that names an item.</summary>
    private async Task ShowPriceHistoryAsync(ListingSearch search)
    {
        _priceHistory.Visibility = Visibility.Collapsed;
        if (search.ItemType is null && search.AspectId is null && search.Name is null)
            return; // the whole market's prices say nothing
        var history = await _client.PriceHistoryAsync(search);
        if (history.Days.Count == 0)
            return;
        _priceHistory.Show(history);
        _priceHistory.Visibility = Visibility.Visible;
    }

    private async Task RefreshMineAsync()
    {
        if (!_client.SignedIn)
            return;
        await Run(async () =>
        {
            var listings = await _client.MineAsync();
            _allMine = listings;
            ShowMine();
        });
    }

    /// <summary>The player's listings through the name filter and status choice, in the chosen order.</summary>
    private void ShowMine()
    {
        int Waiting(Listing l) => _waitingOffers.GetValueOrDefault(l.Id);
        var words = _mineFilter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _allMine.Where(l => words.All(w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) && _mineShow.SelectedIndex switch
        {
            1 => Waiting(l) > 0,
            2 => !l.Reserved,
            3 => l.Reserved,
            _ => true,
        });
        shown = _mineSort.SelectedIndex switch
        {
            1 => shown.OrderByDescending(l => l.PriceGold ?? -1),           // best offer last
            2 => shown.OrderBy(l => l.PriceGold ?? long.MaxValue),
            3 => shown.OrderBy(l => l.Expires),
            4 => shown.OrderByDescending(Waiting).ThenByDescending(l => l.Created),
            _ => shown.OrderByDescending(l => l.Created),
        };
        _myListings.Clear();
        foreach (var listing in shown)
            _myListings.Add(listing);
        _noListings.Text = _allMine.Count == 0 ? "You have no active listings." : "No listings match.";
        _noListings.Visibility = _myListings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSellPreview()
    {
        _sellPreview.Children.Clear();
        var draft = _lastItem is { } item ? TooltipListing.From(item, SelectedRealm, null) : null;
        if (draft is null)
            _sellPreview.Children.Add(new TextBlock { Text = "Hover an item in Diablo IV: the last one Deckard reads appears here.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        else
            _sellPreview.Children.Add(ItemPanel(draft.Name, draft.Rarity, draft.ItemType, draft.ItemPower, draft.Ancestral, draft.AspectId, draft.Affixes, SelectedRealm));
        _list.IsEnabled = draft is not null && _client.SignedIn;
        if (draft is null)
        {
            (_similarFor, _similarPrices, _similar.Text) = (null, null, "");
            WarnAboutPrice();
        }
        else if (_shown == (int)TradePage.Listings)
            _ = CheckSimilarAsync(draft); // only while My listings shows: every item read would spend the player's requests
    }

    /// <summary>Queues an item read in game for listing; says what happened, for the item card.</summary>
    public string Queue(ItemTooltip item)
    {
        if (TooltipListing.From(item, SelectedRealm, null) is not { } draft)
            return item.AccountBound ? "Account bound: it can't be traded." : "Deckard didn't read this item well enough to list it.";
        var key = System.Text.Json.JsonSerializer.Serialize(draft);
        if (_queue.Any(q => System.Text.Json.JsonSerializer.Serialize(q.Draft) == key))
            return $"Already queued ({_queue.Count} to list).";
        if (_queue.Count >= MaxQueued)
            return $"The queue holds {MaxQueued}: list those first, in Deckard's Trade tab.";
        _queue.Add(new QueuedItem(draft, new TextBox { PlaceholderText = "best offer", Width = 130 }));
        ShowQueue();
        return $"Queued: {_queue.Count} to list in Deckard's Trade tab.";
    }

    /// <summary>The queued items with a price box each, and buttons to suggest prices and to list them all.</summary>
    private void ShowQueue()
    {
        _queuePanel.Children.Clear();
        _queuePanel.Children.Add(new TextBlock { Text = _queue.Count > 0 ? $"Queued to list ({_queue.Count})" : "Queued to list", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        if (_queue.Count == 0)
        {
            _queuePanel.Children.Add(new TextBlock { Text = "Press Queue on the item card over the game to gather items here, then price and list them at once.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            return;
        }
        foreach (var queued in _queue)
        {
            var d = queued.Draft;
            var remove = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetToolTip(remove, "Take it off the queue");
            remove.Click += (_, _) => { _queue.Remove(queued); ShowQueue(); };
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(), new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } } };
            var panel = ItemPanel(d.Name, d.Rarity, d.ItemType, d.ItemPower, d.Ancestral, d.AspectId, d.Affixes, SelectedRealm);
            row.Children.Add(panel);
            Grid.SetColumn(queued.Price, 1);
            queued.Price.VerticalAlignment = remove.VerticalAlignment = VerticalAlignment.Top;
            if (queued.Price.Parent is Panel old)
                old.Children.Remove(queued.Price); // the box moves to the redrawn row, keeping what was typed
            row.Children.Add(queued.Price);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _queuePanel.Children.Add(Bordered(row));
        }
        var suggest = new Button { Content = "Suggest prices" };
        ToolTipService.SetToolTip(suggest, "Fills empty price boxes with what similar items sold or are listed for");
        suggest.Click += async (_, _) => await Run(async () =>
        {
            foreach (var queued in _queue.Where(q => q.Price.Text.Trim().Length == 0).ToList())
            {
                var prices = await _client.PriceCheckAsync(TooltipListing.Similar(queued.Draft with { Realm = SelectedRealm }));
                if ((prices.SoldMedianGold ?? prices.MedianGold) is { } median)
                    queued.Price.Text = Gold.Format(median);
            }
        });
        var listAll = new Button { Content = "List all", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        listAll.Click += async (_, _) => await ListQueueAsync();
        _queuePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { suggest, listAll } });
    }

    /// <summary>Lists every queued item at its price (an empty box is best offer), stopping at the first that fails.</summary>
    private async Task ListQueueAsync()
    {
        if (_queue.FirstOrDefault(q => q.Price.Text.Trim().Length > 0 && Gold.Parse(q.Price.Text) is null) is { } bad)
        {
            _message.Text = $"Write {bad.Draft.Name}'s price like 750k, 25M or 2.5B.";
            return;
        }
        var listed = 0;
        await Run(async () =>
        {
            foreach (var queued in _queue.ToList())
            {
                await _client.CreateAsync(queued.Draft with { Realm = SelectedRealm, PriceGold = Gold.Parse(queued.Price.Text) });
                _queue.Remove(queued);
                listed++;
            }
        });
        var failed = _message.Text;
        ShowQueue();
        _message.Text = $"Listed {listed} item{(listed == 1 ? "" : "s")}." + (failed.Length > 0 ? " " + failed : "");
        await RefreshMineAsync();
    }

    /// <summary>
    /// What items like the one to sell go for, under its preview; an empty price box takes the recent sales' median,
    /// else the asking median, as a starting point.
    /// </summary>
    private async Task CheckSimilarAsync(NewListing draft)
    {
        var key = System.Text.Json.JsonSerializer.Serialize(draft);
        if (!_client.SignedIn || key == _similarFor)
            return;
        _similarFor = key;
        try
        {
            var prices = await _client.PriceCheckAsync(TooltipListing.Similar(draft));
            if (key != _similarFor)
                return; // another item came in meanwhile
            _similarPrices = prices;
            _similar.Text = "Similar items: " + OverlayWindow.PriceText(prices);
            if (_price.Text.Trim().Length == 0 && (prices.SoldMedianGold ?? prices.MedianGold) is { } median)
                _price.Text = Gold.Format(median);
            WarnAboutPrice();
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
        {
            _similarFor = null; // try again next time
        }
    }

    /// <summary>A price far under or over what similar items go for gets a warning before it's listed.</summary>
    private void WarnAboutPrice()
    {
        var median = _similarPrices?.SoldMedianGold ?? _similarPrices?.MedianGold;
        var price = Gold.Parse(_price.Text);
        _priceWarning.Text = (price, median) switch
        {
            ({ } p, { } m) when p * 2 < m => $"Well under what similar items go for ({Gold.Format(m)}): check the price before listing.",
            ({ } p, { } m) when p > m * 3 => $"Well over what similar items go for ({Gold.Format(m)}): it may not sell.",
            _ => "",
        };
        _priceWarning.Foreground = price < median ? ItemCard.Bad : ItemCard.Fair;
        _priceWarning.Visibility = _priceWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A rune, gem, elixir or material: its name, the kind, how many, and the realm.</summary>
    private static StackPanel GoodsPanel(Listing listing)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = listing.Quantity > 1 ? $"{listing.Name} ×{listing.Quantity}" : listing.Name,
            FontFamily = ItemCard.BoldFont, FontSize = 17, Foreground = (Brush)Application.Current.Resources["TitleBrush"], TextWrapping = TextWrapping.Wrap,
        });
        var realm = listing.Realm.Kind == RealmKind.Eternal ? "Eternal" : $"Season {listing.Realm.Season}";
        panel.Children.Add(new TextBlock { Text = $"{listing.ItemType} · {realm}{(listing.Realm.Hardcore ? " Hardcore" : "")}", Opacity = 0.75, FontSize = 12 });
        return panel;
    }

    /// <summary>"Sell runes, gems and materials": pick the kind and name, say how many and the price for them all.</summary>
    private StackPanel GoodsForm()
    {
        var kind = new ComboBox { Header = "Kind", MinWidth = 160 };
        var name = new AutoSuggestBox { Header = "Name", PlaceholderText = "Click or type to pick one", Width = 260 };
        var quantity = new TextBox { Header = "How many", Text = "1", Width = 90 };
        var price = new TextBox { Header = "Price for them all (gold)", PlaceholderText = "e.g. 50M", Width = 200 };
        var list = new Button { Content = "List them", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Bottom };
        WireGoodsPicker(name, () => kind.SelectedItem as string);
        kind.DropDownOpened += (_, _) =>
        {
            if (kind.Items.Count == 0)
                foreach (var k in _catalog?.GoodsKinds ?? [])
                    kind.Items.Add(k);
        };
        kind.SelectionChanged += (_, _) => name.Text = "";
        list.Click += async (_, _) =>
        {
            if (kind.SelectedItem is not string k || !GoodsNames(k, "").Contains(name.Text))
            {
                _message.Text = "Pick what to sell from the list.";
                return;
            }
            if (!int.TryParse(quantity.Text, out var count) || count < 1)
            {
                _message.Text = "Say how many, from 1.";
                return;
            }
            if (Gold.Parse(price.Text) is not { } gold)
            {
                _message.Text = "Write the price like 750k, 25M or 2.5B.";
                return;
            }
            await Run(async () =>
            {
                await _client.CreateAsync(new NewListing(SelectedRealm, k, name.Text, "Common", 0, false, null, [], gold, count));
                _message.Text = $"Listed {count} {name.Text}.";
                (name.Text, quantity.Text, price.Text) = ("", "1", "");
                await RefreshMineAsync();
            });
        };
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                new TextBlock { Text = "Sell runes, gems and materials", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                kind, name,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { quantity, price } },
                list,
            },
        };
    }

    private async Task ListLastItemAsync()
    {
        if (_lastItem is null)
            return;
        var price = Gold.Parse(_price.Text);
        if (_price.Text.Trim().Length > 0 && price is null)
        {
            _message.Text = "Write the price like 750k, 25M or 2.5B.";
            return;
        }
        if (TooltipListing.From(_lastItem, SelectedRealm, price) is not { } listing)
            return;
        // Off while the server answers, and for this item once it's listed: a second press listed it twice. The focus goes
        // to the price beside it first: left on a disabled button, it jumped to the next control down and scrolled there.
        _price.Focus(FocusState.Programmatic);
        _list.IsEnabled = false;
        _message.Text = $"Listing {listing.Name}…";
        var listed = false;
        await Run(async () =>
        {
            await _client.CreateAsync(listing);
            listed = true;
            _message.Text = $"Listed {listing.Name}.";
            (_price.Text, _similarFor) = ("", null);
            await RefreshMineAsync();
        });
        if (!listed)
            _list.IsEnabled = _client.SignedIn;
    }

    /// <summary>A listing as an item card, with the seller's BattleTag to copy, or the seller's own controls.</summary>
    private const int BuildFitSort = 4; // the sort list's "Best fit for my build"
    private Deckard.Core.Builds.BuildVariant? _variant;

    /// <summary>How a listing compares with the loaded build's item for its slot; null without a build or a plan for its type.</summary>
    private Deckard.Perception.Builds.ItemComparison? Fit(Listing listing) =>
        _variant is { } variant && _catalog is { } catalog
            ? Deckard.Perception.Builds.BuildMatcher.CompareListed(listing.ItemType, listing.Name, listing.AspectId, listing.Affixes.Select(a => a.Id), variant, catalog)
            : null;

    /// <summary>Builds the result cards again (they're made as they scroll into view, with the build fit of that moment).</summary>
    private void RedrawResults()
    {
        var listings = _listings.ToList();
        _listings.Clear();
        foreach (var listing in listings)
            _listings.Add(listing);
    }

    /// <summary>"Build fit 75% · Craft", coloured by the verdict, with its reason and the missing affixes as the tooltip.</summary>
    private TextBlock? FitLine(Listing listing)
    {
        if (Fit(listing) is not { } fit)
            return null;
        var verdict = fit.Verdict;
        var line = new TextBlock
        {
            Text = $"Build fit {fit.Score:P0}" + (verdict is { } v ? $" · {v.Verdict}" : ""),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = verdict?.Verdict switch
            {
                Deckard.Perception.Builds.ItemVerdict.Keep => ItemCard.Good,
                Deckard.Perception.Builds.ItemVerdict.Craft => ItemCard.Fair,
                _ => ItemCard.Bad,
            },
        };
        var missing = fit.Missing.Select(e => e.Lines[0].Replace("#", "…")).ToList();
        ToolTipService.SetToolTip(line, string.Join("\n", new[] { verdict?.Reason, missing.Count > 0 ? "Missing: " + string.Join(", ", missing) : null }.OfType<string>()));
        return line;
    }

    private Border Card(Listing listing, bool mine)
    {
        var wanted = mine ? null : _search?.Affixes?.Select(a => a.Id).ToHashSet();
        var goods = IsGoodsKind(listing.ItemType);
        var panel = goods ? GoodsPanel(listing) : ItemPanel(listing.Name, listing.Rarity, listing.ItemType, listing.ItemPower, listing.Ancestral, listing.AspectId, listing.Affixes, listing.Realm, wanted);
        if (!mine && FitLine(listing) is { } fitLine)
            panel.Children.Insert(1, fitLine); // under the name
        // Price, age and seller trim on narrow cards; the buttons always stay whole on the right.
        var detail = (goods && listing.Quantity > 1 && listing.PriceGold is { } total ? $"  · {Gold.Format(total / listing.Quantity)} each" : "") + $"  · {Age(listing.Created)}" + (listing.Reserved ? "  · reserved: offer accepted" : "") + (listing.SellerInGame && !mine ? "  · seller in game" : "");
        if (mine)
        {
            var sold = new Button { Content = "Sold" };
            var withdraw = new Button { Content = "Withdraw" };
            sold.Click += async (_, _) => await Run(async () => { await _client.MarkSoldAsync(listing.Id); await RefreshMineAsync(); });
            withdraw.Click += async (_, _) => await Run(async () => { await _client.WithdrawAsync(listing.Id); await RefreshMineAsync(); });
            var left = listing.Expires - DateTimeOffset.UtcNow;
            detail += left <= TimeSpan.Zero ? "  · expired" : $"  · expires in {Left(left)}";
            Button[] actions = left < RenewWithin ? [RenewButton(listing), sold, withdraw] : [sold, withdraw];
            panel.Children.Add(Footer(Price(listing.PriceGold), detail, actions));
            // Standing offers stand out: an accent border and a count under the name.
            if (_waitingOffers.GetValueOrDefault(listing.Id) is var waiting and > 0)
            {
                var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                panel.Children.Insert(1, new TextBlock
                {
                    Text = waiting == 1 ? "● 1 offer waiting" : $"● {waiting} offers waiting",
                    Foreground = accent,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                var card = Bordered(panel);
                card.BorderBrush = accent;
                card.BorderThickness = new Thickness(2);
                return card;
            }
        }
        else
            panel.Children.Add(Footer(Price(listing.PriceGold), detail, OfferButton(listing)));
        var result = Bordered(panel);
        if (!mine)
            result.ContextFlyout = BlockMenu(new BlockRequest(ListingId: listing.Id), "Hide this seller");
        return result;
    }

    /// <summary>
    /// Right-click menu that blocks the player behind a listing, offer or buy order, both ways (lifted in Settings);
    /// the market is searched again without them.
    /// </summary>
    private MenuFlyout BlockMenu(BlockRequest request, string label)
    {
        var block = new MenuFlyoutItem { Text = label };
        block.Click += async (_, _) => await Run(async () =>
        {
            await _client.BlockAsync(request);
            _message.Text = "Blocked: you and they no longer see each other's listings, buy orders or offers. Settings can undo it.";
            await SearchAsync(page: 0);
            await PollOffersAsync(notify: false);
        });
        return new MenuFlyout { Items = { block } };
    }

    private static string Price(long? gold) => gold is { } g ? $"{Gold.Format(g)} gold" : "Best offer";

    /// <summary>A card's bottom line: the bold part and details that trim on narrow cards, with whole buttons on the right.</summary>
    private static Grid Footer(string bold, string detail, params Button[] buttons)
    {
        var info = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        info.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = bold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        info.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = detail });
        ToolTipService.SetToolTip(info, bold + detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            actions.Children.Add(button);
        var footer = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = { new(), new() { Width = GridLength.Auto } } };
        footer.Children.Add(info);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        return footer;
    }

    private static Border Bordered(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(14, 10, 14, 10),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["SurfaceBrush"],
        BorderBrush = (Brush)Application.Current.Resources["SurfaceBorderBrush"],
        MaxWidth = 640,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>"Make offer": the asking price or less (any amount on a best-offer listing), sent from a small flyout.</summary>
    private Button OfferButton(Listing listing)
    {
        var amount = new TextBox { Header = "Your offer (gold)", Text = listing.PriceGold is { } gold ? Gold.Format(gold) : "", PlaceholderText = "e.g. 20M", Width = 200 };
        var send = new Button { Content = "Send offer", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var note = new TextBlock { Text = listing.PriceGold is { } max ? $"Up to {Gold.Format(max)}." : "Any amount.", Opacity = 0.7, FontSize = 12 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { amount, note, send } } };
        send.Click += async (_, _) =>
        {
            if (Gold.Parse(amount.Text) is not { } price)
            {
                note.Text = "Write the amount like 750k, 25M or 2.5B.";
                return;
            }
            flyout.Hide();
            await Run(async () =>
            {
                await _client.MakeOfferAsync(listing.Id, price);
                _message.Text = $"Offered {Gold.Format(price)} gold for {listing.Name}. You'll see the seller's BattleTag if they accept.";
                await PollOffersAsync(notify: false);
            });
        };
        return new Button { Content = "Make offer", Flyout = flyout };
    }

    private void CopyBattleTag(string battleTag)
    {
        var data = new DataPackage();
        data.SetText(battleTag);
        Clipboard.SetContent(data);
        _message.Text = $"Copied {battleTag}: add them as a friend in game to trade.";
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer StartPolling()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = PollInterval;
        timer.Tick += async (_, _) => await PollOffersAsync();
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Fetches the player's offers, redraws the Offers tab and its count, and raises a <see cref="Notice"/> for each
    /// offer that is new on the player's listings or newly accepted on theirs.
    /// </summary>
    private async Task PollOffersAsync(bool notify = true)
    {
        if (!_client.SignedIn || _polling)
            return;
        _polling = true;
        try
        {
            await ReportPresenceAsync();
            var box = await _client.OffersAsync();
            ShowOffers(box);
            if (notify && _polls++ % 240 == 0)
                await WarnExpiringAsync(); // on the first poll, then about hourly (polls come every 15 s)
            if (notify && _polls % 8 == 1)
                await CheckWatchedAsync(); // about every two minutes
            foreach (var offer in box.Received.Concat(box.Sent))
            {
                var known = _seenOffers.TryGetValue(offer.Id, out var before);
                _seenOffers[offer.Id] = offer.Status;
                if (!notify || (known && before == offer.Status))
                    continue;
                if (box.Received.Contains(offer) && offer.Status == OfferStatus.Pending)
                    Notice?.Invoke(new TradeNotice(
                        $"Offer: {Gold.Format(offer.PriceGold)} gold", ListingPanel(offer.Listing),
                        VersusAsking(offer.PriceGold, offer.Listing.PriceGold),
                        [new("Accept", () => AcceptAsync(offer)), new("Decline", () => DeclineAsync(offer))],
                        offer.PriceGold < offer.Listing.PriceGold ? ItemCard.Bad : null));
                else if (box.Sent.Contains(offer) && offer.Status == OfferStatus.Countered && offer.CounterGold is { } asks)
                    Notice?.Invoke(new TradeNotice(
                        $"Counter-offer: {Gold.Format(asks)} gold", ListingPanel(offer.Listing), $"You offered {Gold.Format(offer.PriceGold)} gold.",
                        [new($"Accept {Gold.Format(asks)}", () => Run(async () => { await _client.AcceptOfferAsync(offer.Id); await PollOffersAsync(notify: false); }))]));
                else if (box.Sent.Contains(offer) && offer.Status == OfferStatus.Accepted && offer.Counterpart is { } seller)
                    Notice?.Invoke(new TradeNotice(
                        "Offer accepted", ListingPanel(offer.Listing), $"Your {Gold.Format(offer.PriceGold)} gold offer · seller {seller}",
                        [new("Copy BattleTag", () => { CopyBattleTag(seller); return Task.CompletedTask; })]));
            }
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
        {
            // A missed poll is retried on the next tick; the Offers tab keeps what it showed.
        }
        finally
        {
            _polling = false;
        }
    }

    private bool _inGame;
    private string? _zone;
    private bool _presenceShared; // presence went to the server and hasn't been cleared since

    /// <summary>Whether Diablo IV is running, from the item feed; reported with every poll.</summary>
    public void SetInGame(bool inGame) => _inGame = inGame;

    /// <summary>The zone the game shows, read from the screen; shared only when the settings say so.</summary>
    public void SetZone(string? zone) => _zone = zone;

    private Realm? _askedRealm; // asked once per realm read, not again on every character select

    /// <summary>
    /// The realm of the character picked in game: when the Trade tab is set to another, the player is asked to switch, as
    /// an Eternal listing is out of a Seasonal character's reach (and the other way round).
    /// </summary>
    public void SetGameRealm(Realm realm)
    {
        var index = (realm.Kind, realm.Hardcore) switch
        {
            (RealmKind.Eternal, false) => 0,
            (RealmKind.Eternal, true) => 1,
            (RealmKind.Seasonal, false) => 2,
            _ => 3,
        };
        if (index == _realm.SelectedIndex || index >= _realm.Items.Count || realm == _askedRealm)
            return;
        _askedRealm = realm;
        var name = (string)_realm.Items[index];
        var current = (string)_realm.Items[_realm.SelectedIndex];
        Notice?.Invoke(new TradeNotice(
            $"Your character plays {name}",
            new TextBlock { Text = $"Trade is set to {current}.", FontFamily = ItemCard.Font, FontSize = 14, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap },
            $"Listings and searches in {current} are out of {("AEIOU".Contains(name[0]) ? "an" : "a")} {name} character's reach. Switch the Trade tab to {name}?",
            [new($"Switch to {name}", () => { _realm.SelectedIndex = index; return Task.CompletedTask; })]));
    }

    /// <summary>Tells the server whether the player is in game, whether to show it, and the zone when shared.</summary>
    /// <remarks>
    /// Sent only while the player shows as online or shares their location: otherwise the server would keep a log of
    /// when they play that they never opted into. Turning both off sends one last "not in game" to clear what's there.
    /// </remarks>
    private async Task ReportPresenceAsync()
    {
        var settings = AppSettings.Shared;
        var sharing = settings.ShowOnline || settings.ShareLocation;
        if (!sharing && !_presenceShared)
            return;
        try
        {
            await _client.UpdatePresenceAsync(sharing
                ? new Presence(_inGame, settings.ShowOnline, settings.ShareLocation ? _zone : null, settings.ShareLocation)
                : new Presence(InGame: false, ShowOnline: false));
            _presenceShared = sharing;
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
        {
            // Presence is best effort: the next poll reports it again.
        }
    }

    /// <summary>How an offer compares with the asking price, e.g. "5M (17%) below your 30M asking price."</summary>
    internal static string VersusAsking(long offer, long? ask) => ask switch
    {
        null => "Listed for best offer.",
        var a when offer >= a => $"Your full asking price of {Gold.Format(a.Value)}.",
        var a => $"{Gold.Format(a.Value - offer)} ({(a.Value - offer) * 100.0 / a.Value:0}%) below your {Gold.Format(a.Value)} asking price.",
    };

    private StackPanel ListingPanel(Listing l) => ItemPanel(l.Name, l.Rarity, l.ItemType, l.ItemPower, l.Ancestral, l.AspectId, l.Affixes, l.Realm);

    private Task AcceptAsync(Offer offer) => Run(async () =>
    {
        var accepted = await _client.AcceptOfferAsync(offer.Id);
        _message.Text = $"Accepted. {accepted.Counterpart} can now see your BattleTag; add each other in game to trade.";
        await PollOffersAsync(notify: false);
    });

    private Task DeclineAsync(Offer offer) => Run(async () =>
    {
        await _client.DeclineOfferAsync(offer.Id);
        await PollOffersAsync(notify: false);
    });

    /// <summary>The Offers page: offers on the player's listings on the left, the player's own on the right; the rail counts open ones.</summary>
    private void ShowOffers(OfferBox box)
    {
        var waiting = box.Received.Count(o => o.Status == OfferStatus.Pending);
        if (waiting != _waiting)
            WaitingOffersChanged?.Invoke(_waiting = waiting);
        var perListing = box.Received.Where(o => o.Status == OfferStatus.Pending).GroupBy(o => o.Listing.Id).ToDictionary(g => g.Key, g => g.Count());
        if (perListing.Count != _waitingOffers.Count || perListing.Any(p => _waitingOffers.GetValueOrDefault(p.Key) != p.Value))
        {
            _waitingOffers = perListing;
            ShowMine(); // redraw My listings with the new counts
        }
        // Polled every 15 s: redraw only when something about the offers changed (their ages tick on at the next change).
        var shown = string.Join("|", box.Received.Concat(box.Sent).Select(o =>
            $"{o.Id}:{o.Status}:{o.Updated:O}:{o.MyDone}:{o.CounterpartInGame}:{o.CounterpartZone}:{o.CounterpartRecord}")) + $"/{box.Received.Count}";
        if (shown == _offersShown)
            return;
        _offersShown = shown;
        Fill(_received, box.Received, _noReceived);
        Fill(_sent, box.Sent, _noSent);
    }

    private Border OfferCard(Offer offer, bool received)
    {
        var l = offer.Listing;
        var panel = ListingPanel(l);
        // An accepted deal leads with the other player's BattleTag: it's what they need now.
        var bold = offer.Counterpart is { } other ? $"Deal with {other}" : $"{Gold.Format(offer.PriceGold)} gold offered";
        var asked = (offer.Counterpart is null ? "" : $"  · {Gold.Format(offer.PriceGold)} gold")
            + (l.PriceGold is { } ask ? $"  · asking {Gold.Format(ask)}" : "  · best offer");
        var counter = offer.CounterGold is { } c ? Gold.Format(c) : "";
        var status = offer.Status switch
        {
            OfferStatus.Pending => received ? "  · waiting for you" : "  · waiting for the seller",
            OfferStatus.Countered => received ? $"  · you countered {counter}" : $"  · the seller asks {counter}",
            OfferStatus.Accepted => offer.MyDone ? "  · you marked it completed" : "",
            OfferStatus.Declined => "  · declined",
            OfferStatus.Withdrawn => "  · withdrawn",
            _ => "  · deal cancelled",
        };
        panel.Children.Add(Footer(bold, asked + status + $"  · {Age(offer.Updated)}"));
        panel.Children.Add(new TextBlock { Text = TraderLine(offer, received), Opacity = 0.8, FontSize = 13, TextWrapping = TextWrapping.Wrap });

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Add(string label, Func<Task> action, bool accent = false)
        {
            var button = new Button { Content = label };
            if (accent)
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += async (_, _) => await action();
            buttons.Children.Add(button);
            return button;
        }
        Task Then(Func<Task> call) => Run(async () => { await call(); await PollOffersAsync(notify: false); });
        var mine = offer.CounterGold ?? offer.PriceGold;
        switch (offer.Status, received)
        {
            case (OfferStatus.Pending, true):
                Add("Accept", () => AcceptAsync(offer), accent: true);
                buttons.Children.Add(AmountButton("Counter", "Your counter (gold)", l.PriceGold,
                    l.PriceGold is { } top ? $"More than {Gold.Format(offer.PriceGold)}, up to {Gold.Format(top)}." : $"More than {Gold.Format(offer.PriceGold)}.",
                    price => Then(() => _client.CounterAsync(offer.Id, price))));
                Add("Decline", () => DeclineAsync(offer));
                break;
            case (OfferStatus.Countered, true):
                Add("Decline", () => DeclineAsync(offer));
                break;
            case (OfferStatus.Pending or OfferStatus.Countered, false):
                if (offer.Status == OfferStatus.Countered)
                    Add($"Accept {counter}", () => Then(() => _client.AcceptOfferAsync(offer.Id)), accent: true);
                buttons.Children.Add(AmountButton("Change offer", "Your offer (gold)", mine,
                    l.PriceGold is { } most ? $"Up to {Gold.Format(most)}." : "Any amount.", price => Then(() => _client.ReviseOfferAsync(offer.Id, price))));
                Add("Withdraw", () => Then(() => _client.WithdrawOfferAsync(offer.Id)));
                break;
            case (OfferStatus.Accepted, _) when offer.Counterpart is { } tag:
                Add("Copy BattleTag", () => { CopyBattleTag(tag); return Task.CompletedTask; }, accent: true);
                Add(offer.MyDone ? "Completed ✓" : "Mark completed", () => Then(() => _client.CompleteDealAsync(offer.Id))).IsEnabled = !offer.MyDone;
                buttons.Children.Add(ReportButton(offer, Then));
                Add("Cancel deal", () => Then(() => _client.CancelDealAsync(offer.Id)));
                break;
            case (OfferStatus.Cancelled, _):
                buttons.Children.Add(ReportButton(offer, Then));
                break;
        }
        if (buttons.Children.Count > 0)
            panel.Children.Add(buttons);
        var card = Bordered(panel);
        if (offer.Status is OfferStatus.Declined or OfferStatus.Withdrawn or OfferStatus.Cancelled)
            card.Opacity = 0.6;
        card.ContextFlyout = BlockMenu(new BlockRequest(OfferId: offer.Id), received ? "Block this buyer" : "Block this seller");
        return card;
    }

    /// <summary>"Buyer: 12 completed trades, no reports · in game · in Kyovashad": the other player's record and presence.</summary>
    private static string TraderLine(Offer offer, bool received)
    {
        var role = received ? "Buyer" : "Seller";
        var record = offer.CounterpartRecord is { } r && (r.Trades > 0 || r.Reports > 0)
            ? $"{r.Trades} completed trade{(r.Trades == 1 ? "" : "s")}, {(r.Reports == 0 ? "no reports" : $"{r.Reports} report{(r.Reports == 1 ? "" : "s")}")}"
            : "new trader, no record yet";
        return $"{role}: {record}" + (offer.CounterpartInGame ? " · in game" : "") + (offer.CounterpartZone is { } zone ? $" · in {zone}" : "");
    }

    /// <summary>A button that asks for an amount of gold in a flyout, then sends it.</summary>
    private static Button AmountButton(string label, string header, long? initial, string note, Func<long, Task> send)
    {
        var amount = new TextBox { Header = header, Text = initial is { } gold ? Gold.Format(gold) : "", PlaceholderText = "e.g. 20M", Width = 200 };
        var hint = new TextBlock { Text = note, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        var go = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { amount, hint, go } } };
        go.Click += async (_, _) =>
        {
            if (Gold.Parse(amount.Text) is not { } price)
            {
                hint.Text = "Write the amount like 750k, 25M or 2.5B.";
                return;
            }
            flyout.Hide();
            await send(price);
        };
        return new Button { Content = label, Flyout = flyout };
    }

    /// <summary>Report a deal that went wrong, in the player's words; it counts against the other player's record.</summary>
    private Button ReportButton(Offer offer, Func<Func<Task>, Task> then)
    {
        var reason = new TextBox { Header = "What went wrong?", PlaceholderText = "e.g. never showed up in game", Width = 260, MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var go = new Button { Content = "Send report", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { reason, go } } };
        go.Click += async (_, _) =>
        {
            if (reason.Text.Trim().Length == 0)
                return;
            flyout.Hide();
            await then(() => _client.ReportDealAsync(offer.Id, reason.Text.Trim()));
            _message.Text = "Thanks: the report counts on that player's record.";
        };
        return new Button { Content = "Report", Flyout = flyout };
    }

    /// <summary>Name in its rarity's colour, the type line, the aspect or unique power, then the affix lines with their rolls.</summary>
    private StackPanel ItemPanel(string name, string rarity, string itemType, int power, bool ancestral, long? aspectId, IReadOnlyList<ListedAffix> affixes, Realm realm, HashSet<long>? wanted = null)
    {
        var panel = new StackPanel { Spacing = 2 };
        var color = ItemCard.RarityColors.TryGetValue(rarity, out var c) ? c : Microsoft.UI.Colors.White;
        panel.Children.Add(new TextBlock { Text = name, FontFamily = ItemCard.BoldFont, FontSize = 17, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap });
        var realmText = realm.Kind == RealmKind.Eternal ? "Eternal" : $"Season {realm.Season}";
        panel.Children.Add(new TextBlock { Text = $"{(ancestral ? "Ancestral " : "")}{rarity} {itemType} · {power} Item Power · {realmText}{(realm.Hardcore ? " Hardcore" : "")}", Opacity = 0.75, FontSize = 12 });
        if (aspectId is { } id && _catalog?.FindById(id) is { } powerEntry)
            panel.Children.Add(new TextBlock { Text = powerEntry.Name ?? powerEntry.Lines[0], FontFamily = ItemCard.Font, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap });
        foreach (var affix in affixes)
        {
            var line = _catalog?.FindById(affix.Id)?.Lines[0] is { } template ? Fill(template, affix.Value) : $"affix {affix.Id}: {affix.Value}";
            var text = new TextBlock
            {
                Text = (affix.Greater ? "★ " : "") + line + (affix.Slot == AffixSlot.Tempered ? "  (tempered)" : ""),
                FontFamily = ItemCard.Font,
                Opacity = affix.Slot == AffixSlot.Implicit ? 0.75 : 1,
                TextWrapping = TextWrapping.Wrap,
            };
            // The lines the search asked for stand out, so a match reads at a glance.
            if (wanted?.Contains(affix.Id) == true)
            {
                text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                text.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            }
            panel.Children.Add(text);
        }
        return panel;
    }

    /// <summary>The template with its first # replaced by the rolled value, as the tooltip showed it.</summary>
    private static string Fill(string template, double value)
    {
        var at = template.IndexOf('#');
        return at < 0 ? template : template[..at] + value.ToString("0.#", CultureInfo.InvariantCulture) + template[(at + 1)..];
    }

    private const int MaxWatched = 5;
    private readonly Dictionary<string, DateTimeOffset> _watchedSince = []; // per watched search: the newest listing already seen

    /// <summary>
    /// Searches each watched quick search, newest first, and shows a notice for listings newer than the last check (at
    /// most three per search at a time). The first check of a search only notes where to start from.
    /// </summary>
    private async Task CheckWatchedAsync()
    {
        foreach (var watched in _saved.Where(s => s.Watched).Take(MaxWatched).ToList())
        {
            var page = await _client.SearchAsync(watched.Search with { Realm = SelectedRealm, Page = 0, Sort = ListingSort.Newest });
            // The server's clock, never this PC's: an empty page starts from the beginning, so the next listing counts.
            var newest = page.Listings.Select(l => l.Created).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            if (!_watchedSince.TryGetValue(watched.Name, out var since))
            {
                _watchedSince[watched.Name] = newest;
                continue;
            }
            foreach (var listing in page.Listings.Where(l => l.Created > since).Take(3))
                Notice?.Invoke(new TradeNotice(
                    $"New: {watched.Name}", ListingPanel(listing),
                    listing.PriceGold is { } price ? $"Asking {Gold.Format(price)} gold. Make an offer in Deckard's Trade tab." : "Listed for best offer.",
                    []));
            _watchedSince[watched.Name] = newest > since ? newest : since;
        }
    }

    /// <summary>
    /// The Buy orders page: the player's own orders (cancel while open) on the left, other players' open orders in the
    /// realm (and the item type the search filters name) on the right, each of which takes the last item read if it fits.
    /// </summary>
    private async Task RefreshWantedAsync()
    {
        if (!_client.SignedIn)
            return;
        await Run(async () =>
        {
            var mine = await _client.MyWantedAsync();
            var others = await _client.BrowseWantedAsync(new ListingSearch(SelectedRealm, SelectedType));
            Fill(_myOrders, mine, _noMyOrders);
            _buyingTitle.Text = SelectedType is { } type ? $"Players buying: {type}" : "Players buying";
            Fill(_otherOrders, others.Wanted, _noOtherOrders);
        });
    }

    private Border WantedCard(Wanted order)
    {
        var panel = WantPanel(order.Want);
        var detail = $"  · {Age(order.Created)}";
        var buttons = new List<Button>();
        if (order.Mine)
        {
            detail += order.Status switch
            {
                WantedStatus.Open => $"  · open, {Left(order.Expires - DateTimeOffset.UtcNow)} left",
                WantedStatus.Filled => "  · bought: the deal is in Offers",
                _ => "  · cancelled",
            };
            if (order.Status == WantedStatus.Open)
            {
                var cancel = new Button { Content = "Cancel" };
                cancel.Click += async (_, _) => await Run(async () => { await _client.CancelWantedAsync(order.Id); await RefreshWantedAsync(); });
                buttons.Add(cancel);
            }
        }
        else
        {
            var sell = new Button { Content = "Sell last item read", IsEnabled = _lastItem is not null };
            ToolTipService.SetToolTip(sell, "Sells the last item Deckard read to this buyer at this price, if it fits; you then see each other's BattleTags.");
            sell.Click += async (_, _) => await Run(async () =>
            {
                if (_lastItem is null || TooltipListing.From(_lastItem, SelectedRealm, null) is not { } item)
                {
                    _message.Text = "Hover the item in Diablo IV first; account-bound items can't be sold.";
                    return;
                }
                var deal = await _client.FillWantedAsync(order.Id, item);
                _message.Text = $"Sold {item.Name} for {Gold.Format(deal.PriceGold)} gold to {deal.Counterpart}: add each other in game to trade.";
                await RefreshWantedAsync();
                await PollOffersAsync(notify: false);
            });
            buttons.Add(sell);
        }
        panel.Children.Add(Footer($"{Gold.Format(order.PriceGold)} gold", detail, [.. buttons]));
        var card = Bordered(panel);
        if (order.Status != WantedStatus.Open)
            card.Opacity = 0.6;
        if (!order.Mine)
            card.ContextFlyout = BlockMenu(new BlockRequest(WantedId: order.Id), "Block this buyer");
        return card;
    }

    /// <summary>What a buy order asks for: a name, power or type, then the item's other filters and the affix rolls.</summary>
    private StackPanel WantPanel(ListingSearch want)
    {
        var panel = new StackPanel { Spacing = 2 };
        var power = want.AspectId is { } id ? _catalog?.FindById(id) : null;
        var title = want.Name ?? power?.Name ?? want.ItemType ?? "Any item";
        panel.Children.Add(new TextBlock { Text = title, FontFamily = ItemCard.BoldFont, FontSize = 17, Foreground = (Brush)Application.Current.Resources["TitleBrush"], TextWrapping = TextWrapping.Wrap });
        var realm = want.Realm.Kind == RealmKind.Eternal ? "Eternal" : $"Season {want.Realm.Season}";
        string?[] parts =
        [
            want.Rarity, want.ItemType != title ? want.ItemType : null, want.MinItemPower is { } ip ? $"{ip}+ Item Power" : null,
            want.Class is { } c ? $"for {c}" : null, want.MinGreaterAffixes > 0 ? $"{want.MinGreaterAffixes}+ greater affixes" : null,
            realm + (want.Realm.Hardcore ? " Hardcore" : ""),
        ];
        panel.Children.Add(new TextBlock { Text = string.Join(" · ", parts.OfType<string>()), Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var filter in want.Affixes ?? [])
        {
            var template = _catalog?.FindById(filter.Id)?.Lines[0] ?? $"affix {filter.Id}: #";
            var rolls = (filter.Min, filter.Max) switch
            {
                ({ } min, { } max) => $"{min:0.#}–{max:0.#}",
                ({ } min, null) => $"{min:0.#}+",
                (null, { } max) => $"up to {max:0.#}",
                _ => "…",
            };
            panel.Children.Add(new TextBlock { Text = (filter.Greater ? "★ " : "") + template.Replace("#", rolls), FontFamily = ItemCard.Font, TextWrapping = TextWrapping.Wrap });
        }
        return panel;
    }

    /// <summary>Listings this close to expiring offer Renew; those under <see cref="WarnWithin"/> get a notice.</summary>
    private static readonly TimeSpan RenewWithin = TimeSpan.FromDays(3), WarnWithin = TimeSpan.FromDays(1);
    private readonly HashSet<Guid> _warnedExpiry = [];
    private int _polls;

    private Button RenewButton(Listing listing)
    {
        var renew = new Button { Content = "Renew", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        renew.Click += async (_, _) => await Run(async () =>
        {
            await _client.RenewAsync(listing.Id);
            _message.Text = $"{listing.Name} is listed for another 14 days.";
            await RefreshMineAsync();
        });
        return renew;
    }

    /// <summary>A notice for each of the player's listings with under a day left, once per listing while Deckard runs.</summary>
    private async Task WarnExpiringAsync()
    {
        // Still up: one that expired already (My listings keeps it, to renew) was announced as coming off "in 1 min", at every start.
        var now = DateTimeOffset.UtcNow;
        var soon = (await _client.MineAsync()).Where(l => l.Expires > now && l.Expires - now < WarnWithin && _warnedExpiry.Add(l.Id));
        foreach (var listing in soon)
            Notice?.Invoke(new TradeNotice(
                "Listing about to expire", ListingPanel(listing), $"{listing.Name} comes off the market in {Left(listing.Expires - DateTimeOffset.UtcNow)}.",
                [new("Renew", () => Run(async () => { await _client.RenewAsync(listing.Id); await RefreshMineAsync(); }))]));
    }

    // Rounded, not cut: a listing made a moment ago has 14 days less a few seconds left, which cut read "13 d" beside
    // another's "14 d" with the same end.
    private static string Left(TimeSpan t) => t.TotalHours < 1 ? $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min"
        : t.TotalDays < 1 ? $"{(int)Math.Round(t.TotalHours)} h" : $"{(int)Math.Round(t.TotalDays)} d";

    private static string Age(DateTimeOffset created) => (DateTimeOffset.UtcNow - created) switch
    {
        var t when t.TotalMinutes < 1 => "just now",
        var t when t.TotalMinutes < 60 => $"{(int)t.TotalMinutes} min ago",
        var t when t.TotalHours < 24 => $"{(int)t.TotalHours} h ago",
        var t => $"{(int)t.TotalDays} d ago",
    };

    /// <summary>Runs a server call, showing what went wrong instead of throwing (server down, validation, limits).</summary>
    private async Task Run(Func<Task> action)
    {
        try
        {
            _message.Text = "";
            await action();
        }
        catch (TradeException e)
        {
            _message.Text = e.Message;
        }
        catch (HttpRequestException)
        {
            _message.Text = "Can't reach the trade server.";
        }
        catch (TaskCanceledException)
        {
            _message.Text = "The trade server took too long to answer.";
        }
    }
}

/// <summary>The Trade pages, each an entry in the main window's rail.</summary>
public enum TradePage
{
    Search,
    Listings,
    Offers,
    BuyOrders,
}
