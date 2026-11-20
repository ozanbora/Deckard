using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Deckard.App;

/// <summary>
/// Overlays show only while the game is the active window, and while the overlay key (Alt by default, like the game's
/// own Alt details) is held, or after it's pressed in toggle mode (see <see cref="AppSettings"/>). Alt+Tab hides them
/// because the game stops being the active window. Polls the foreground window and the key's state ~30 times a second
/// on the UI thread; it reads key state only and sends nothing to the game.
/// </summary>
public sealed partial class OverlayVisibility
{
    private readonly DispatcherQueueTimer _timer;
    private nint _game;
    private bool _wasDown, _toggled;

    public OverlayVisibility(DispatcherQueue ui, ItemFeed feed)
    {
        feed.GameWindowChanged += hwnd => ui.TryEnqueue(() => _game = hwnd);
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) =>
        {
            var settings = AppSettings.Shared;
            var front = GetForegroundWindow();
            var inGame = _game != 0 && (front == _game || (front == Card && Card != 0)); // the card has the focus while the pointer is on it
            var state = GetAsyncKeyState(settings.OverlayKey);
            var down = (state & 0x8000) != 0;
            // A tap quicker than the poll (~33 ms) is down and up again between two reads: the low bit says it was pressed.
            var tapped = !down && (state & 1) != 0;
            if (!settings.ToggleOverlay)
                _toggled = false;
            else if ((down || tapped) && !_wasDown && inGame && !Hold)
                _toggled = !_toggled; // a press in the game flips it; presses elsewhere don't count
            _wasDown = down;
            var visible = inGame && (Hold || (settings.ToggleOverlay ? _toggled : down));
            if (visible == IsVisible && inGame == InGame)
                return;
            (IsVisible, InGame) = (visible, inGame);
            Changed?.Invoke(visible);
        };
        _timer.Start();
    }

    public bool IsVisible { get; private set; }

    /// <summary>The game (or the item card) is the active window, key or not: the skill tree and paragon highlights need only this.</summary>
    public bool InGame { get; private set; }

    /// <summary>
    /// The player is typing on the item card (correcting a value): overlays stay up whatever the overlay key does, and
    /// the key, pressed while typing, toggles nothing.
    /// </summary>
    public bool Hold { get; set; }

    /// <summary>The item card's window, which counts as the game while it has the focus.</summary>
    public nint Card { get; set; }

    /// <summary>Raised on the UI thread when overlays should appear or disappear (<see cref="IsVisible"/> or <see cref="InGame"/> changed).</summary>
    public event Action<bool>? Changed;

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
}
