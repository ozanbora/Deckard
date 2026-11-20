using System.Diagnostics;
using System.Globalization;
using Deckard.Capture;
using Deckard.Perception;
using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Deckard.Perception.Imaging;
using Deckard.Perception.Mercenaries;
using Deckard.Perception.Paragon;
using Deckard.Perception.Realms;
using Deckard.Perception.SkillTrees;
using Deckard.Perception.Ocr;
using Deckard.Perception.Tooltips;
using Deckard.Perception.Zones;

namespace Deckard.App;

/// <summary>
/// Runs capture, tooltip watching, OCR and parsing on a background thread. Events are raised on that thread;
/// windows marshal them to the UI thread themselves.
/// </summary>
public sealed partial class ItemFeed : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<string>? StatusChanged;

    /// <summary>An item was read; the comparison is against <see cref="Plan"/> (null without a plan or a matching slot).</summary>
    public event Action<ItemTooltip, ItemComparison?>? ItemRead;

    /// <summary>The season's item data couldn't be loaded; nothing else runs. The message says why.</summary>
    public event Action<string>? LoadFailed;

    /// <summary>A tooltip with the "EQUIPPED" banner was read (a comparison, or the character screen): an item the player wears.</summary>
    public event Action<ItemTooltip>? EquippedRead;

    /// <summary>The season's item data is loaded; build plans can be shown from now on.</summary>
    public event Action<AffixCatalog>? Ready;

    /// <summary>Build variant read items are compared against. Set from the UI thread, read on the feed's thread.</summary>
    public BuildVariant? Plan { get; set; }

    /// <summary>The account's runes by key and count, as their tooltips were last read; null when none has been read yet.</summary>
    public IReadOnlyDictionary<string, int>? OwnedRunes { get; set; }

    /// <summary>The account's corrections of misread values (see <see cref="ReadCorrections"/>), applied to each read; set by the app.</summary>
    public IReadOnlyDictionary<string, ItemCorrection>? Corrections { get; set; }

    /// <summary>The player corrected an item's values, or undid it (null): its key and what to keep. Raised on the UI thread.</summary>
    public event Action<string, ItemCorrection?>? CorrectionChanged;

    /// <summary>
    /// The player corrected a read item (on the UI thread): the correction is kept, and the item goes out again as read,
    /// so its card, build fit, stash verdict and listing all follow.
    /// </summary>
    public void Correct(ItemTooltip item, AffixCatalog catalog)
    {
        CorrectionChanged?.Invoke(ReadCorrections.ItemKey(item), ReadCorrections.Of(item, DateTimeOffset.UtcNow));
        ItemRead?.Invoke(item, Plan is { } plan ? BuildMatcher.Compare(item, plan, catalog) : null);
    }

    /// <summary>A rune's tooltip was read: its key and how many the player has. Raised on the feed's thread.</summary>
    public event Action<string, int>? RuneRead;

    /// <summary>
    /// The hovered tooltip is a rune's: the rune (null when it couldn't be told), how many the tooltip says, and how many
    /// were saved before (null: none), for the card. Raised on the feed's thread.
    /// </summary>
    public event Action<RuneSighting>? RuneSeen;

    /// <summary>Another tooltip replaced the one last read, before it's read itself. Raised on the feed's thread.</summary>
    public event Action? TooltipReplaced;

    /// <summary>The skill points spent last read for this build (saved per account), for the docked panel, which doesn't show them.</summary>
    public int? RememberedSpent { get; set; }

    /// <summary>The full skill tree's points spent were read, for the app to keep. Raised on the feed's thread.</summary>
    public event Action<int>? SpentRead;

    /// <summary>The account's skills' item ranks (node → ranks), as their tooltips were last read; set by the app.</summary>
    public IReadOnlyDictionary<int, int>? SkillBonuses { get; set; }

    /// <summary>A skill's item ranks were read off its tooltip: node and ranks. Raised on the feed's thread.</summary>
    public event Action<int, int>? SkillBonusRead;

    /// <summary>The account's skills' ranks (node → rank) as the tree last showed them; set by the app.</summary>
    public IReadOnlyDictionary<int, int>? SkillRanks { get; set; }

    /// <summary>A node's rank as the tree shows it changed: node and rank. Raised on the feed's thread.</summary>
    public event Action<int, int>? SkillRankRead;

    /// <summary>
    /// A game panel opened or closed on the right (Character, Abilities): its left edge in game client pixels, or 0.
    /// Raised on the feed's thread when it changes.
    /// </summary>
    public event Action<int>? SidePanelChanged;

    /// <summary>
    /// The in-game highlights pick the leveling step from the points spent (<see cref="Plan"/> then has every step);
    /// otherwise they show the plan's last step, the one the player chose.
    /// </summary>
    public bool AutoSkillStep { get; set; }

    public bool AutoParagonStep { get; set; }

    /// <summary>
    /// The automatic steps last read for this build (saved per account), used until the points spent are read again: the
    /// docked panels don't show them.
    /// </summary>
    public int? RememberedSkillStep { get; set; }

    public int? RememberedParagonStep { get; set; }

    /// <summary>An automatic step was picked from the points spent: (paragon, step index). Raised on the feed's thread.</summary>
    public event Action<bool, int>? AutoStepChosen;

    /// <summary>The Diablo IV window being read, or 0 while the game isn't running.</summary>
    public event Action<nint>? GameWindowChanged;

    /// <summary>
    /// Where the build's nodes are on the open in-game skill tree or paragon screen; null when it closes. Raised only on
    /// change.
    /// </summary>
    public event Action<TreeHighlight?>? HighlightChanged;

    /// <summary>The zone the game shows above the minimap, when it changes; read only while location sharing is on.</summary>
    public event Action<string>? ZoneRead;

    /// <summary>The realm of the character picked on the character select screen, when it changes.</summary>
    public event Action<Deckard.Core.Trade.Realm>? RealmRead;

    private TextRecognizer? zoneOcr; // made on first use: most players don't share their location

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        TooltipReader reader;
        AffixCatalog catalog;
        try
        {
            StatusChanged?.Invoke("Loading the game data…");
            catalog = await GameData.LoadAsync(TradeClient.Shared);
            reader = new TooltipReader(catalog);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException or NotSupportedException)
        {
            StatusChanged?.Invoke(e.Message);
            LoadFailed?.Invoke(e.Message);
            return;
        }
        Ready?.Invoke(catalog);

        var watcher = new TooltipWatcher(new TooltipLocator());
        var skillTooltips = new TooltipLocator(); // whether a skill's tooltip is open on the tree
        BuildVariant? treeFor = null;
        SkillTree? tree = null;
        SkillTreeLocator? treeLocator = null;
        TreePlacement? shown = null, pending = null;
        Task<Dictionary<long, Deckard.Core.Capture.Frame>>? treeIcons = null; // the build's skill icons, for the class check
        bool? classMatches = null; // whether the open tree is the build's class; decided once per opening
        SkillRankReader? skillRanks = null; // made on first use, with the zone reader's OCR engine
        ItemTooltip? lastRead = null; // the last item read, which a scrolled read of the same tooltip adds to
        int? autoSkill = null, autoParagon = null; // the steps picked from the points spent
        int? treeSpent = null, treeAvailable = null; // the skill tree's "points spent" and "Available Points", while they read
        var sidePanelLeft = 0; // a game panel open on the right: its left edge, else 0
        int? treeHovered = null; // the skill the cursor rests on, whose tooltip is open
        var sincePoints = Stopwatch.StartNew();
        var sinceMercenaries = Stopwatch.StartNew();
        var shownMercenaries = ""; // the advice on the Mercenaries tab last posted, "" for none
        var sinceRanks = Stopwatch.StartNew();
        BuildVariant? paragonFor = null;
        ParagonStep? paragonStep = null;
        ParagonLocator? paragonLocator = null;
        ParagonPlacement? shownGrid = null;
        string? treeState = null, shownState = null;
        var baseStatus = "";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var hwnd = WindowLocator.FindByProcessName(WindowLocator.Diablo4ProcessName);
                GameWindowChanged?.Invoke(hwnd);
                if (hwnd == 0)
                {
                    StatusChanged?.Invoke("Waiting for Diablo IV…");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                try
                {
                    await using var source = await WgcFrameSource.CreateForWindowAsync(hwnd);
                    source.Start();
                    baseStatus = $"Reading tooltips · season {catalog.Season} (game build {catalog.GameBuild})"
                        // The server's copy couldn't be fetched: the saved one may lack what this version shows.
                        + (GameData.Problem is { } problem ? $" · using saved game data, {problem}" : "");
                    StatusChanged?.Invoke(baseStatus);
                    var sinceCheck = Stopwatch.StartNew();
                    var sinceZone = Stopwatch.StartNew();
                    string? lastZone = null;
                    var sinceRealm = Stopwatch.StartNew();
                    Deckard.Core.Trade.Realm? lastRealm = null;
                    long blackSince = 0; // when the frames turned black (0: they aren't), read by the readiness watch
                    _repostStatus = 0;
                    using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // Cancelled when the game's window is gone: Windows doesn't always say the capture closed (a game
                    // relaunched while Deckard ran stayed unread, its last status frozen), so the watch checks.
                    using var capturing = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var watch = WatchReadinessAsync(hwnd, () => Interlocked.Read(ref blackSince), baseStatus, capturing.Cancel, watching.Token);
                    try
                    {
                        await foreach (var captured in source.Frames.ReadAllAsync(capturing.Token))
                        {
                            using (captured)
                            {
                                // Windowed with a title bar, the game's picture starts below it: every measurement is
                                // relative to the game's own picture, so read only that.
                                // shortcut: copies the picture each frame in windowed mode, crop in the capture if it ever costs too much.
                                using var cropped = WindowLocator.ClientArea(hwnd) is { Area: > 0 } client
                                    && client != new Deckard.Core.Capture.PixelRect(0, 0, captured.Width, captured.Height) ? captured.Crop(client) : null;
                                var frame = cropped ?? captured;
                                // Measured at 3440x1440: the panels' left edge at x 2540, 0.625 of the height from the right.
                                // Not the full skill tree or paragon screen: their header is as dark, but they fill the screen.
                                var sidePanel = SkillTreeLocator.DockedPanelDark(frame) && SkillTreeLocator.SidePanelTitled(frame) && SkillTreeLocator.Layout(frame) != ScreenLayout.Full
                                    && ParagonLocator.Layout(frame) != ScreenLayout.Full ? frame.Width - (int)(frame.Height * 0.625) : 0;
                                if (sidePanel != sidePanelLeft)
                                    SidePanelChanged?.Invoke(sidePanelLeft = sidePanel);
                                Interlocked.Exchange(ref blackSince, !ImageOps.IsBlack(frame) ? 0 : blackSince != 0 ? blackSince : Environment.TickCount64);
                                if (Interlocked.Exchange(ref _repostStatus, 0) == 1)
                                    shownState = ""; // never a state: a readiness problem just cleared, so post this loop's full status again
                                // Skill tree: every frame (~3 ms), so the highlight follows a drag. Only changes are posted.
                                var plan = Plan;
                                if (plan != treeFor)
                                {
                                    treeFor = plan;
                                    tree = plan is not null && plan.ClassIndex < catalog.Classes.Count ? catalog.SkillTrees.GetValueOrDefault(catalog.Classes[plan.ClassIndex]) : null;
                                    treeLocator = tree is null ? null : new SkillTreeLocator(tree);
                                    if (treeIcons is { IsCompletedSuccessfully: true })
                                        foreach (var icon in treeIcons.Result.Values)
                                            icon.Dispose();
                                    treeIcons = tree is null ? null : LoadSkillIconsAsync(tree);
                                    classMatches = null;
                                    shown = null;
                                    (autoSkill, autoParagon, treeSpent) = (RememberedSkillStep, RememberedParagonStep, RememberedSpent);
                                    HighlightChanged?.Invoke(null);
                                }

                                // Automatic steps: the points spent (the panel on the skill tree and the paragon screen)
                                // pick the leveling step to work toward, read once a second while either screen shows.
                                var stepChanged = false;
                                var treeOpen = SkillTreeLocator.IsOpen(frame);
                                var paragonOpen = !treeOpen && ParagonLocator.Layout(frame) == ScreenLayout.Full;
                                if (plan is not null && (treeOpen && AutoSkillStep || paragonOpen && AutoParagonStep) && sincePoints.ElapsedMilliseconds >= 1000)
                                {
                                    sincePoints.Restart();
                                    zoneOcr ??= TextRecognizer.Create();
                                    if (await PointsSpent.ReadAsync(frame, zoneOcr) is (int spent, int available))
                                    {
                                        if (treeOpen && spent != treeSpent)
                                            SpentRead?.Invoke(spent);
                                        if (treeOpen && (spent != treeSpent || available != treeAvailable))
                                            (treeSpent, treeAvailable, stepChanged) = (spent, available, true); // what the points cover and what's left to mark moved
                                        if (treeOpen && plan.SkillSteps.Count > 0
                                            && PointsSpent.StepFor([.. plan.SkillSteps.Select(s => s.RanksByNode.Values.Sum())], spent, available) is var skillStep && skillStep != autoSkill)
                                        {
                                            autoSkill = skillStep;
                                            stepChanged = true;
                                            AutoStepChosen?.Invoke(false, skillStep);
                                        }
                                        if (paragonOpen && plan.ParagonSteps.Count > 0
                                            && PointsSpent.StepFor([.. plan.ParagonSteps.Select(s => s.Boards.Sum(b => b.Nodes.Count))], spent, available) is var paragonIndex && paragonIndex != autoParagon)
                                        {
                                            autoParagon = paragonIndex;
                                            paragonFor = null; // the step's boards are the locator's
                                            AutoStepChosen?.Invoke(true, paragonIndex);
                                        }
                                    }
                                    // The tree docked in the Abilities panel: its box has the points available, no points
                                    // spent. The spent count last read on the full tree stands; the step moves with it. Only
                                    // docked: on the full tree its box holds nodes, whose "1/15" read as a count.
                                    else if (treeOpen && SkillTreeLocator.Layout(frame) == ScreenLayout.Docked
                                        && await PointsSpent.ReadDockedAsync(frame, zoneOcr) is int docked && docked != treeAvailable)
                                    {
                                        (treeAvailable, stepChanged) = (docked, true);
                                        if (treeSpent is int known && plan.SkillSteps.Count > 0
                                            && PointsSpent.StepFor([.. plan.SkillSteps.Select(s => s.RanksByNode.Values.Sum())], known, docked) is var dockedStep && dockedStep != autoSkill)
                                        {
                                            autoSkill = dockedStep;
                                            AutoStepChosen?.Invoke(false, dockedStep);
                                        }
                                    }
                                }
                                var skillStepIndex = plan is null || plan.SkillSteps.Count == 0 ? -1
                                    : AutoSkillStep && autoSkill is int chosen && chosen < plan.SkillSteps.Count ? chosen
                                    : plan.SkillSteps.Count - 1;
                                var skillRanksPlan = skillStepIndex < 0 ? new Dictionary<int, int>() : plan!.SkillSteps[skillStepIndex].RanksByNode;
                                var placement = treeLocator?.Locate(frame);
                                // The cursor, in frame pixels; only its position is read, nothing is sent to the game.
                                var (hasCursor, cursorX, cursorY) = OverlayWindow.Native.TryGetCursor(out var cursorAt) && OverlayWindow.Native.TryGetClientOrigin(hwnd, out var originX, out var originY)
                                    ? (true, (double)(cursorAt.X - originX), (double)(cursorAt.Y - originY)) : (false, -1.0, -1.0);
                                // A skill's tooltip covers the tree and throws the locator off (measured: placed 4,000 px
                                // away, or not at all). It shows only while the cursor rests on a skill, so the tree as
                                // last placed holds while the cursor is on one of its skills.
                                var restingOn = shown is { } held && hasCursor && tree is not null ? SkillRankReader.SkillAt(frame, held, tree.Nodes, cursorX, cursorY)?.Node : null;
                                // Only with a tooltip really open beside the cursor (found like an item's; the game world
                                // can hold a false one far off): after a drag the cursor can rest where a skill was, and the
                                // old placement held put the marks off screen ("1 more below"). Docked, the tooltip opens
                                // left of the panel and the tree still locates: nothing to hold.
                                var band = frame.Height * 0.6;
                                if (restingOn is not null && (shown?.Layout == ScreenLayout.Docked
                                    || !skillTooltips.Locate(frame).Any(t => t.Right > cursorX - band && t.X < cursorX + band)))
                                    restingOn = null;
                                if (restingOn is not null)
                                    placement = shown;
                                // Its tooltip covers other marks, which drew over its text: while it's open only its own
                                // mark shows, and they all come back when the cursor moves on.
                                var hoverChanged = restingOn != treeHovered;
                                treeHovered = restingOn;
                                // Any class's tree fits the screen by layout; the skill icons tell whether it's the build's
                                // class. Unsure (icons still loading, few skills in view) keeps the highlight. A no is
                                // checked again on later frames: one view can judge poorly (a tree mid-way through opening).
                                if (placement is { } located && classMatches is not true && treeIcons is { IsCompletedSuccessfully: true }
                                    && SkillIconCheck.Similarity(frame, located, tree!, treeIcons.Result) is { } similarity)
                                    classMatches = similarity >= SkillIconCheck.Threshold;
                                else if (placement is null && !SkillTreeLocator.IsOpen(frame))
                                    classMatches = null;
                                // A "no" only warns (in the status line): the icon check has rejected the build's own
                                // tree on some views (a Warlock tree centred on its Fire hub scored 0.04-0.08, under other
                                // classes' 0.09), and hiding the right build's marks is worse than a wrong build's showing.
                                // Show a highlight only once two frames in a row agree (the locator returns the same
                                // placement for a still tree): the first fit can catch the tree mid-way through opening.
                                if (placement is null)
                                    pending = null;
                                else if (shown is null && placement != pending)
                                    (pending, placement) = (placement, null);
                                // The ranks the player has: a few skills' rank text read every half second while the tree
                                // shows, so skills at the step's rank lose their marks like taken paragon nodes.
                                var ranksChanged = false;
                                if (shown is { } still && placement == shown && sinceRanks.ElapsedMilliseconds >= 500)
                                {
                                    sinceRanks.Restart();
                                    zoneOcr ??= TextRecognizer.Create();
                                    if (skillRanks is null)
                                    {
                                        // Item ranks read in an earlier session hold, so skills needn't all be hovered again.
                                        skillRanks = new SkillRankReader(zoneOcr);
                                        skillRanks.Remember(SkillBonuses ?? new Dictionary<int, int>(), SkillRanks);
                                        skillRanks.BonusRead += (node, bonus) => SkillBonusRead?.Invoke(node, bonus);
                                        skillRanks.RankRead += (node, rank) => SkillRankRead?.Invoke(node, rank);
                                    }
                                    var buildSkills = BuildSkills(tree!, skillRanksPlan);
                                    // Not while a tooltip is open: on the full tree it covers nodes, docked its lower boxes hang
                                    // over the tree's left edge, and taken modifiers under it read as not taken (Skull Splitter
                                    // while Rampage was clicked). The cursor on a skill means its tooltip; a modifier's, or one
                                    // still fading, is found by its border beside the cursor.
                                    var tipBand = frame.Height * 0.6;
                                    var underTooltip = hasCursor && (SkillRankReader.SkillAt(frame, still, buildSkills, cursorX, cursorY) is not null
                                        || skillTooltips.Locate(frame).Any(t => t.Right > cursorX - tipBand && t.X < cursorX + tipBand));
                                    if (!underTooltip)
                                        ranksChanged = await skillRanks.ReadAsync(frame, still, buildSkills, cursorX: cursorX, cursorY: cursorY);
                                    // The skill the cursor rests on: its tooltip says how many of its ranks are items'.
                                    if (hasCursor)
                                    {
                                        var tipBefore = skillRanks.LastTooltip;
                                        ranksChanged |= await skillRanks.ReadTooltipAsync(frame, still, buildSkills, cursorX, cursorY);
                                        ranksChanged |= skillRanks.LastTooltip != tipBefore; // shown in the status line
                                    }
                                }
                                else if (placement is null && !SkillTreeLocator.IsOpen(frame))
                                    skillRanks?.Forget();
                                if (placement != shown || ranksChanged || (stepChanged || hoverChanged) && placement is not null)
                                {
                                    shown = placement;
                                    // What the step adds over what the points spent cover; ranks read off the tree refine it
                                    // (they include gear, charm and rune ranks, which SkillPoints.ToSpend allows for).
                                    // With the points available read: exactly where those points go next, in the guide's order.
                                    int? SpentOn(int node) => skillRanks?.Spent(node);
                                    bool Exact(int node) => skillRanks?.Bonus(node) is not null;
                                    // Only with a count read: an unread one (-1) or none to spend marks what's left in the step.
                                    // Docked, the spent count may be unknown: then the step before the chosen one counts as done.
                                    // An unread count (-1) still names the next point: a blank tree read as nothing to do.
                                    int? toPlace = treeAvailable is int a && (a > 0 || a == -1) ? Math.Max(1, a) : null;
                                    var next = skillStepIndex >= 0 && AutoSkillStep && toPlace is int left
                                        ? SkillPoints.NextPoints(plan!.SkillSteps, skillStepIndex, treeSpent, left, SpentOn, Exact) : null;
                                    // The step's nodes all done with points to spend: item ranks put the tree ahead of the
                                    // guide's totals (Apocalypse 15 spent at 49, the step's 52), or docked the spent count is
                                    // the last one read. The points go to the next step that still wants some.
                                    while (next is { Count: 0 } && skillStepIndex < plan!.SkillSteps.Count - 1)
                                        next = SkillPoints.NextPoints(plan.SkillSteps, ++skillStepIndex, treeSpent, toPlace!.Value, SpentOn, Exact);
                                    var toSpend = skillStepIndex < 0 ? new Dictionary<int, int>()
                                        : next?.ToDictionary(n => n.Node, n => n.Target)
                                            ?? SkillPoints.ToSpend(plan!.SkillSteps, skillStepIndex, AutoSkillStep ? treeSpent : null, SpentOn, Exact);
                                    string NodeName(int node) => tree!.Nodes.FirstOrDefault(n => n.Node == node)?.Name ?? node.ToString(CultureInfo.InvariantCulture);
                                    var nextText = next is null ? null
                                        : next.Count == 0 ? (treeAvailable > 0 ? "No point to spend in this step" : null)
                                        : "Next point" + (next.Sum(n => n.Points) > 1 ? "s" : "") + ": " + string.Join(", ", next.Select(n => n.Points > 1 ? $"{NodeName(n.Node)} ×{n.Points}" : NodeName(n.Node)))
                                            + $" (step {skillStepIndex + 1})";
                                    var highlight = placement is { } p ? Highlight(hwnd, frame, p, tree!, toSpend, treeHovered, nextText) : null;
                                    // Why these marks: the step, the points spent and each node with the rank the tree shows.
                                    var why = (nextText is null ? "" : nextText + "; ") + $"step {skillStepIndex + 1}, {(treeSpent is int ts ? ts.ToString(CultureInfo.InvariantCulture) : "?")} spent: "
                                        + string.Join(", ", toSpend.Select(t => $"{tree!.Nodes.FirstOrDefault(n => n.Node == t.Key)?.Name ?? t.Key.ToString(CultureInfo.InvariantCulture)} "
                                            + $"{(skillRanks?.Spent(t.Key) is int r ? r.ToString(CultureInfo.InvariantCulture) : "?")}"
                                            + $"{(skillRanks?.Bonus(t.Key) is int b and > 0 ? $"+{b.ToString(CultureInfo.InvariantCulture)}" : "")}/{t.Value.ToString(CultureInfo.InvariantCulture)}"))
                                        + (skillRanks?.LastTooltip is { } tip ? "; tooltip: " + tip : "");
                                    HighlightChanged?.Invoke(highlight);
                                    treeState = highlight is null ? null : $"skill tree located, zoom {placement!.Value.Zoom:F2}, {highlight.Marks.Count(m => !m.Ghost)} build nodes to take in view ({why})"
                                        // The docked panel doesn't show the points spent the automatic step goes by.
                                        + (placement.Value.Layout == ScreenLayout.Docked && AutoSkillStep ? "; the step is from the full skill tree, open it to update" : "");
                                }

                                // Paragon: the chosen step's nodes on the boards attached in game, every frame like the tree.
                                if (plan != paragonFor)
                                {
                                    paragonFor = plan;
                                    paragonStep = plan is null || plan.ParagonSteps.Count == 0 ? null
                                        : AutoParagonStep && autoParagon is int pick && pick < plan.ParagonSteps.Count ? plan.ParagonSteps[pick]
                                        // Automatic, but no points read yet (the docked panel shows only those available): the
                                        // last step's boards, without its glyph levels, which said "level 150" at 12 points.
                                        : AutoParagonStep ? plan.ParagonSteps[^1] with { Boards = [.. plan.ParagonSteps[^1].Boards.Select(b => b with { GlyphLevel = 0 })] }
                                        : plan.ParagonSteps[^1];
                                    paragonLocator = paragonStep is null ? null : new ParagonLocator(ParagonLocator.Cells(paragonStep, catalog.Paragon));
                                    shownGrid = null;
                                }
                                var grid = placement is null ? paragonLocator?.Locate(frame) : null;
                                // The Board Selection panel dims the boards behind it, so the grid may not be found; which
                                // board to pick needs only the boards seen attached (a pitch of 0: no grid to mark).
                                if (grid is null && placement is null && paragonLocator is not null && ParagonLocator.IsOpen(frame) && ParagonLocator.InBoardSelection(frame))
                                    grid = new ParagonPlacement(0, 0, 0, paragonLocator.SeenAttached, Selecting: true);
                                if (grid != shownGrid)
                                {
                                    shownGrid = grid;
                                    if (placement is null) // the skill tree's highlight, when it's up, is already posted
                                        HighlightChanged?.Invoke(grid is { } g ? ParagonHighlight(hwnd, frame, g, paragonStep!, catalog.Paragon, paragonLocator!) : null);
                                }
                                // The Mercenaries tab: who the player has hired and enlisted against the guide's, once a second
                                // while a panel is open on the right, written under its two boxes.
                                if (placement is null && grid is null && sidePanel > 0 && plan?.Mercenaries is { } mercenaries)
                                {
                                    if (sinceMercenaries.ElapsedMilliseconds >= 500)
                                    {
                                        sinceMercenaries.Restart();
                                        zoneOcr ??= TextRecognizer.Create();
                                        // One of the game's own tooltips over the panel (a hovered node's): nothing is drawn across it.
                                        var covered = skillTooltips.Locate(frame).Any(r => r.Right > frame.Width - frame.Height * 0.625);
                                        var seen = covered ? null : await MercenaryPanel.ReadAsync(frame, zoneOcr, catalog);
                                        // A mercenary's own skill tree: the guide's picks marked on it, in the order to take them.
                                        var picks = seen?.Tree is { } open ? MercenaryPanel.TreePicks(open, mercenaries, catalog, frame.Width, frame.Height, seen.SkillRow) : [];
                                        var (hire, enlist) = seen is null || seen.Tree is not null ? ((string?)null, (string?)null) : MercenaryPanel.Advise(seen, mercenaries, catalog);
                                        var advice = picks.Count > 0 ? $"tree {seen!.Tree} {picks.Count}" : $"{hire}|{enlist}";
                                        if (advice != shownMercenaries && (advice != "|" || shownMercenaries.Length > 0))
                                        {
                                            shownMercenaries = advice == "|" ? "" : advice;
                                            HighlightChanged?.Invoke(picks.Count > 0 ? MercenaryTreeHighlight(hwnd, frame, picks, seen!.SkillRow)
                                                : advice == "|" ? null : MercenaryHighlight(hwnd, frame, hire, enlist));
                                        }
                                    }
                                }
                                else if (shownMercenaries.Length > 0 && placement is null && grid is null)
                                {
                                    shownMercenaries = "";
                                    HighlightChanged?.Invoke(null);
                                }
                                // Say what the highlight sees, so it's clear why nothing shows.
                                var state = classMatches == false && treeState is not null
                                    ? $"{treeState}; this may not be a {catalog.Classes[plan!.ClassIndex]} tree: is the build for this character?"
                                    : treeState ?? (treeLocator is null ? null : SkillTreeLocator.IsOpen(frame) ? "skill tree open but not located" : null)
                                    ?? (grid is { } located2 ? $"paragon located, {System.Numerics.BitOperations.PopCount((uint)located2.Attached)} of {paragonStep!.Boards.Count} build boards attached"
                                        + (AutoParagonStep && autoParagon is null ? "; open the full paragon screen once so the step follows your points" : "")
                                        : paragonLocator is not null && ParagonLocator.IsOpen(frame) ? "paragon open but not located" : null);
                                if (state != shownState)
                                {
                                    shownState = state;
                                    StatusChanged?.Invoke(state is null ? baseStatus : $"{baseStatus} · {state}");
                                }
                                if (placement is not null || grid is not null)
                                    continue; // the skill tree and paragon screens have no item tooltips

                                // The zone above the minimap, every 5 s and only while the player shares their location.
                                // Screens without it (map, menus) read nothing and keep the last zone.
                                if (AppSettings.Shared.ShareLocation && sinceZone.ElapsedMilliseconds >= 5000)
                                {
                                    sinceZone.Restart();
                                    zoneOcr ??= TextRecognizer.Create();
                                    if (await ZoneReader.ReadAsync(frame, zoneOcr) is { } zone && zone != lastZone)
                                        ZoneRead?.Invoke(lastZone = zone);
                                }

                                // The character's realm under its name on the character select screen, every 3 s (a small
                                // strip; anything else on screen reads no realm word and keeps the last one).
                                if (sinceRealm.ElapsedMilliseconds >= 3000)
                                {
                                    sinceRealm.Restart();
                                    zoneOcr ??= TextRecognizer.Create();
                                    if (await RealmReader.ReadAsync(frame, zoneOcr, catalog.Season) is { } realm && realm != lastRealm)
                                        RealmRead?.Invoke(lastRealm = realm);
                                }

                                // Locating costs ~8 ms per frame. A tooltip counts once two checks agree (TooltipWatcher), so
                                // the interval sets how soon after hovering it is read: 100 ms keeps that under ~200 ms.
                                if (sinceCheck.ElapsedMilliseconds < 100)
                                    continue;
                                sinceCheck.Restart();
                                var settled = watcher.Check(frame);
                                if (watcher.Replaced)
                                    TooltipReplaced?.Invoke(); // the card's item is gone: hide it while the new one is read
                                if (settled.Count == 0)
                                    continue;
                                var reading = Stopwatch.StartNew();
                                var tooltips = await reader.ReadAsync(frame);
                                // A rune's tooltip ("TAL (4)", "Unique Rune of Ritual") says how many the player has: it's
                                // saved, and the card says so, or that it couldn't tell which rune.
                                if ((tooltips.FirstOrDefault(t => !t.IsEquipped) ?? tooltips.FirstOrDefault()) is { } hovered && Runewords.IsRune(hovered))
                                {
                                    var (rune, runeCount) = Runewords.Identify(hovered, catalog);
                                    var before = rune is not null && OwnedRunes?.TryGetValue(rune.Key, out var had) == true ? had : (int?)null;
                                    if (rune is not null)
                                        RuneRead?.Invoke(rune.Key, runeCount);
                                    RuneSeen?.Invoke(new RuneSighting(hovered, rune, runeCount, before));
                                }
                                // Something framed like a tooltip with no item power and no known affix isn't an item (the
                                // Shop's cards, a reputation reward's preview "Magic Reputation Cache"; every piece of gear,
                                // white ones too, shows its item power): it doesn't replace the last item read.
                                var runes = tooltips.Where(Runewords.IsRune).ToList();
                                tooltips = [.. tooltips.Where(TooltipReader.IsItem)];
                                // The watcher leaves out what held neither (the game's world framed like a tooltip).
                                foreach (var rect in settled.Where(r => !tooltips.Concat(runes).Any(t => t.Bounds.Intersect(r).Area >= 0.5 * Math.Min(t.Bounds.Area, r.Area))))
                                    watcher.Ignore(rect);
                                // In a comparison the hovered item is the one listing what's lost, or without the "EQUIPPED" banner.
                                if ((tooltips.FirstOrDefault(t => t.Compared) ?? tooltips.FirstOrDefault(t => !t.IsEquipped) ?? tooltips.FirstOrDefault()) is { } read)
                                {
                                    // A long tooltip scrolled further: its lower lines (sockets, binding) join the card.
                                    var item = lastRead is not null && ItemTooltip.MergeScrolled(lastRead, read) is { } merged ? merged : read;
                                    // With a comparison up, the card stands clear of the worn item's tooltip too.
                                    item = item with { Group = tooltips.Count > 1 ? tooltips.Select(t => t.Bounds).Aggregate((a, b) => a.Union(b)) : null };
                                    lastRead = item;
                                    item = ReadCorrections.Apply(item, Corrections); // what the player corrected on this read before
                                    ItemRead?.Invoke(item, plan is not null ? BuildMatcher.Compare(item, plan, catalog) : null);
                                    // How well reading works on this screen: counts and sizes, nothing about the item.
                                    Telemetry.Track("tooltip_read", ("matched", item.Lines.Count(l => l.IsMatched)), ("total", item.Lines.Count),
                                        ("ocr2x", item.Bounds.Width < TooltipReader.SmallTooltipWidth), ("width", frame.Width), ("height", frame.Height),
                                        ("dpi", OverlayWindow.Native.GetDpiForWindow(hwnd)), ("lang", System.Globalization.CultureInfo.CurrentUICulture.Name),
                                        ("ms", reading.ElapsedMilliseconds));
                                }
                                foreach (var worn in tooltips.Where(t => t.IsEquipped))
                                    EquippedRead?.Invoke(worn);
                                StatusChanged?.Invoke($"{baseStatus} · read {tooltips.Count} tooltip(s) in {reading.ElapsedMilliseconds} ms");
                            }
                        }
                        // The channel completes when the game window closes; look for it again.
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // the window is gone (a relaunched game has a new one): look for it again
                    }
                    finally
                    {
                        watching.Cancel();
                        await watch;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    StatusChanged?.Invoke($"Capture stopped: {e.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public const string Minimized = "Diablo IV is minimized: Deckard reads the game from the screen.";
    public const string ExclusiveFullscreen =
        "Deckard can't see Diablo IV in exclusive fullscreen. In the game, set Options → Graphics → Display Mode to Windowed (Fullscreen).";

    /// <summary>
    /// Every 2 s while capturing: says why Deckard can't see the game, which Windows' capture doesn't report (in exclusive
    /// fullscreen it sends black frames or none, and none while the game is minimized), and takes it back once it can;
    /// and calls <paramref name="windowGone"/> once the game's window no longer exists. Only reads window state; never
    /// touches the game.
    /// </summary>
    private async Task WatchReadinessAsync(nint hwnd, Func<long> blackSince, string reading, Action windowGone, CancellationToken ct)
    {
        string? shown = null;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (IsWindow(hwnd) == 0)
                {
                    windowGone(); // the game closed or was relaunched: the loop looks for its window again
                    return;
                }
                var front = GetForegroundWindow() == hwnd;
                string? problem = IsIconic(hwnd) != 0 ? Minimized
                    : front && (SHQueryUserNotificationState(out var state) == 0 && state == RunningD3DFullScreen
                        || blackSince() is var since && since != 0 && Environment.TickCount64 - since > 5000) ? ExclusiveFullscreen
                    : null;
                if (problem == shown)
                    continue;
                shown = problem;
                StatusChanged?.Invoke(problem ?? reading);
                if (problem is null)
                    Interlocked.Exchange(ref _repostStatus, 1); // the loop adds its skill tree state back on its next frame
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private int _repostStatus; // 1: the readiness watch cleared a problem, and the frame loop should post its status again
    private const int RunningD3DFullScreen = 3; // QUNS_RUNNING_D3D_FULL_SCREEN
    [System.Runtime.InteropServices.LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] private static partial int IsIconic(nint hwnd);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] private static partial int IsWindow(nint hwnd);

    /// <summary>The tree's skill icons as frames (downloaded once into the icon cache); icons that fail are left out.</summary>
    private static async Task<Dictionary<long, Deckard.Core.Capture.Frame>> LoadSkillIconsAsync(SkillTree tree)
    {
        var icons = new Dictionary<long, Deckard.Core.Capture.Frame>();
        foreach (var id in tree.Nodes.Where(n => n.Kind == "skill" && n.Icon != 0).Select(n => n.Icon).Distinct())
            if (await IconCache.GetFileAsync(id) is { } path)
                icons[id] = await Deckard.Perception.Imaging.FrameFile.LoadAsync(path);
        return icons;
    }

    /// <summary>
    /// The paragon assistant's marks (<see cref="ParagonGuide"/>) as squares at their place on the captured frame: the
    /// step's nodes, glyph sockets named, and the next board to attach as a pale outline with how to turn it.
    /// </summary>
    private static TreeHighlight ParagonHighlight(nint game, Deckard.Core.Capture.Frame frame, ParagonPlacement grid, ParagonStep step, ParagonData paragon, ParagonLocator seen)
    {
        // A mark is kept clear of the game's panels by its whole square, not just its centre.
        var half = grid.Pitch * 0.42;
        var masked = seen.Masked.Select(r => new Deckard.Core.Capture.PixelRect((int)(r.X - half), (int)(r.Y - half), (int)(r.Width + 2 * half), (int)(r.Height + 2 * half))).ToArray();
        var marks = (grid.Pitch > 0 ? ParagonGuide.Marks(grid, step, paragon, seen.IsCovered, seen.IsTaken) : [])
            .Where(m => m.Kind == GuideKind.Ghost && m.Label is not null // a board's label is kept in view
                || !masked.Any(r => m.X >= r.X && m.X < r.Right && m.Y >= r.Y && m.Y < r.Bottom)) // nothing over the game's panels
            .Select(m => new TreeMark(m.X, m.Y, IsSkill: true, Rank: 0, Ghost: m.Kind == GuideKind.Ghost, Label: m.Label)).ToList();
        // Choosing a board for a gate: which one, beside the game's Board Selection panel at its board name's height.
        if (grid.Selecting && ParagonGuide.PickHint(grid, step, paragon) is { } pick)
        {
            var panel = ParagonLocator.BoardSelection(frame.Width, frame.Height);
            marks.Add(new TreeMark(panel.X - frame.Height * 0.2, frame.Height * 0.367, IsSkill: true, Rank: 0, Ghost: true, Label: pick));
        }
        // The overlay sizes a skill's frame as 0.024 of the height per zoom: make it a little under half a cell.
        return new TreeHighlight(game, frame.Height, ParagonLocator.Panel(frame.Width, frame.Height, grid.Layout), marks, grid.Pitch * 0.42 / (frame.Height * 0.024));
    }

    /// <summary>The advice on the Mercenaries tab: labels under its hired and reinforcement boxes, over the panel only.</summary>
    private static TreeHighlight MercenaryHighlight(nint game, Deckard.Core.Capture.Frame frame, string? hire, string? enlist)
    {
        var (x, hireY, enlistY) = MercenaryPanel.AdviceAt(frame.Width, frame.Height);
        var panel = new Deckard.Core.Capture.PixelRect(frame.Width - (int)(frame.Height * 0.625), 0, (int)(frame.Height * 0.625), frame.Height);
        var marks = new List<TreeMark>();
        if (hire is not null)
            marks.Add(new TreeMark(x, hireY, false, 0, Ghost: true, Label: hire));
        if (enlist is not null)
            marks.Add(new TreeMark(x, enlistY, false, 0, Ghost: true, Label: enlist));
        return new TreeHighlight(game, frame.Height, panel, marks, 1);
    }

    /// <summary>The guide's picks on a mercenary's open skill tree: a frame on each, numbered in the order to take them, and their names under the tree.</summary>
    private static TreeHighlight MercenaryTreeHighlight(nint game, Deckard.Core.Capture.Frame frame, IReadOnlyList<(double X, double Y, int Order, string Name)> picks, bool row)
    {
        var panel = new Deckard.Core.Capture.PixelRect(frame.Width - (int)(frame.Height * 0.625), 0, (int)(frame.Height * 0.625), frame.Height);
        var marks = picks.Select(p => new TreeMark(p.X, p.Y, true, p.Order)).ToList();
        marks.Add(new TreeMark(frame.Width - frame.Height * 0.3125, frame.Height * 0.825, false, 0, Ghost: true,
            Label: row ? $"The guide's reinforcement skill: {picks[0].Name}" : "The guide picks: " + string.Join(", ", picks.Select(p => $"{p.Order} {p.Name}"))));
        return new TreeHighlight(game, frame.Height, panel, marks, row ? 1.45 : 1.15); // the tree's skill nodes are 80 px at 1440, the row's 100
    }

    /// <summary>The build's skills, modifiers and passives at the chosen step, whose ranks are read off the tree.</summary>
    private static List<SkillNode> BuildSkills(SkillTree tree, IReadOnlyDictionary<int, int> ranks) =>
        [.. tree.Nodes.Where(n => n.Kind is "skill" or "modifier" or "passive" && ranks.ContainsKey(n.Node))];

    /// <summary>
    /// The nodes to spend on (<paramref name="ranks"/>, by <see cref="SkillPoints.ToSpend"/>) at their place on the captured
    /// frame. While the cursor rests on a skill (<paramref name="hovered"/>) its tooltip is open: only that skill's own mark
    /// shows, as the others drew over the tooltip's text.
    /// </summary>
    /// <param name="next">Where the next points go, by name, written under the game's points box.</param>
    private static TreeHighlight Highlight(nint game, Deckard.Core.Capture.Frame frame, TreePlacement placement, SkillTree tree, IReadOnlyDictionary<int, int> ranks,
        int? hovered = null, string? next = null)
    {
        if (hovered is int under)
            ranks = ranks.Where(r => r.Key == under).ToDictionary(r => r.Key, r => r.Value);
        var nodes = tree.Nodes.ToDictionary(n => n.Node);
        var marks = new List<TreeMark>();
        // No mark over the game's Skill Assignment bar (bottom centre), kept clear by a skill frame's half and its badge.
        var bar = SkillTreeLocator.AssignmentBar(frame.Width, frame.Height, placement.Layout);
        var clear = frame.Height * 0.024 * placement.Zoom * 1.3;
        var panel = SkillTreeLocator.Panel(frame, placement.Layout);
        int above = 0, below = 0; // the step's nodes out of view or under the bar, which the player has to drag to
        foreach (var (id, rank) in ranks)
        {
            if (!nodes.TryGetValue(id, out var node) || node.Kind is not ("skill" or "modifier" or "passive"))
                continue;
            var (x, y) = placement.ToScreen(node.X, node.Y);
            var half = frame.Height * (node.Kind == "skill" ? 0.024 : 0.0125) * placement.Zoom; // as the overlay draws it
            var underBar = x > bar.X - clear && x < bar.Right + clear && y > bar.Y - clear && y < bar.Bottom + clear;
            if (underBar || x - half < panel.X || y - half < panel.Y || x + half >= panel.Right || y + half >= panel.Bottom)
            {
                if (y < panel.Y + panel.Height / 2.0) above++; else below++;
                continue;
            }
            marks.Add(new TreeMark(x, y, node.Kind == "skill", rank));
        }
        // Label-only hints at the edges: at a close zoom most of a step is out of view, and an empty screen read as nothing to do.
        if (above > 0)
            marks.Add(new TreeMark(panel.X + panel.Width / 2.0, panel.Y, false, 0, Ghost: true, Label: $"{above} more above: drag the tree down"));
        if (below > 0)
        {
            // Left of the bar, level with it: above the bar it covered the bar's button and nodes, the panel's corner was too
            // far off. On the bar's right instead when a mark is there (docked, it covered a marked skill's rank).
            var (hintX, hintY) = (bar.X - frame.Height * 0.12, bar.Y + bar.Height / 2.0);
            if (marks.Any(m => !m.Ghost && Math.Abs(m.X - hintX) < frame.Height * 0.15 && Math.Abs(m.Y - hintY) < frame.Height * 0.08))
                hintX = bar.Right + frame.Height * 0.12;
            marks.Add(new TreeMark(hintX, hintY, false, 0, Ghost: true, Label: $"{below} more below: drag the tree up"));
        }
        // Under the game's points box, so it's read with the points it's about (measured at 3440x1440: full tree x 100-450,
        // y 130-300; docked, the panel's box x 2540-2880, y 185-315).
        if (next is not null && hovered is null)
        {
            var box = placement.Layout == ScreenLayout.Docked ? PointsSpent.DockedBox(frame.Width, frame.Height) : PointsSpent.Panel(frame.Height);
            marks.Add(new TreeMark(box.X + box.Width / 2.0, box.Bottom + frame.Height * 0.02, false, 0, Ghost: true, Label: next));
        }
        return new TreeHighlight(game, frame.Height, panel, marks, placement.Zoom);
    }

    /// <summary>A rune's tooltip as read: the rune, or null when it couldn't be told; its count; the count saved before, if any.</summary>
    public sealed record RuneSighting(ItemTooltip Tooltip, GoodsInfo? Rune, int Count, int? Before);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }
}
