using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Deckard.App;

/// <summary>
/// Deckard's icon in the notification area (click: show or hide Deckard; right-click: Show Deckard, Exit) and the global
/// Ctrl+Shift+D hotkey that does the same from anywhere. Both arrive as messages to the main window, which stays alive
/// while hidden. The hotkey only tells Windows to notify Deckard; nothing is ever sent to the game. While registered,
/// Ctrl+Shift+D goes to Deckard instead of the app in front, the game included.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    public const string HotkeyText = "Ctrl+Shift+D";
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int WmHotkey = 0x0312, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const int NimAdd = 0, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const int HotkeyId = 1, ModControl = 2, ModShift = 4, ModNoRepeat = 0x4000, KeyD = 0x44;
    private const int ShowCommand = 1, ExitCommand = 2;
    private static TrayIcon? _instance; // the window procedure is static; there is one tray icon
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly nint _window;
    private readonly nint _icon;
    private bool _hotkey;

    /// <summary>The icon was clicked or the hotkey pressed: show Deckard, or hide it if it's in front.</summary>
    public event Action? Toggle;
    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayIcon(nint window, string iconFile)
    {
        _window = window;
        _instance = this;
        _icon = LoadImage(0, iconFile, 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50 /* SM_CYSMICON */), 0x10 /* LR_LOADFROMFILE */);
        SetWindowSubclass(window, &WindowProc, 1, 0);
        Add();
    }

    /// <summary>Turns the global hotkey on or off; false when another app already holds the key combination.</summary>
    public bool SetHotkey(bool on)
    {
        if (on == _hotkey)
            return true;
        _hotkey = on && RegisterHotKey(_window, HotkeyId, ModControl | ModShift | ModNoRepeat, KeyD) != 0;
        if (!on)
            UnregisterHotKey(_window, HotkeyId);
        return _hotkey == on;
    }

    private void Add()
    {
        var data = Data();
        data.Flags = NifMessage | NifIcon | NifTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        "Deckard".AsSpan().CopyTo(new Span<char>(data.Tip, 127));
        ShellNotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData Data() => new() { Size = sizeof(NotifyIconData), Window = _window, Id = 1 };

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, ShowCommand, "Show Deckard");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, ExitCommand, "Exit Deckard");
        GetCursorPos(out var cursor);
        SetForegroundWindow(_window); // so the menu closes when the player clicks elsewhere
        var command = TrackPopupMenu(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, cursor.X, cursor.Y, 0, _window, 0);
        DestroyMenu(menu);
        (command == ShowCommand ? ShowRequested : command == ExitCommand ? ExitRequested : null)?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (_instance is { } tray)
            {
                if (message == CallbackMessage)
                {
                    switch ((int)(lParam & 0xFFFF))
                    {
                        case WmLButtonUp: tray.Toggle?.Invoke(); break;
                        case WmRButtonUp or WmContextMenu: tray.ShowMenu(); break;
                    }
                    return 0;
                }
                if (message == WmHotkey && wParam == HotkeyId)
                {
                    tray.Toggle?.Invoke();
                    return 0;
                }
                if (message == TaskbarCreated)
                    tray.Add(); // Explorer restarted: put the icon back
            }
        }
        catch (Exception)
        {
            // An exception can't cross back into Windows here; dropping one click is better than crashing.
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        var data = Data();
        ShellNotifyIcon(NimDelete, ref data);
        SetHotkey(false);
        RemoveWindowSubclass(_window, &WindowProc, 1);
        if (_icon != 0)
            DestroyIcon(_icon);
        _instance = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public int State;
        public int StateMask;
        public fixed char Info[256];
        public int Version;
        public fixed char InfoTitle[64];
        public int InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static partial int ShellNotifyIcon(int message, ref NotifyIconData data);
    [LibraryImport("comctl32.dll")] private static partial int SetWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [LibraryImport("comctl32.dll")] private static partial int RemoveWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [LibraryImport("comctl32.dll")] private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint LoadImage(nint instance, string name, int type, int width, int height, int flags);
    [LibraryImport("user32.dll")] private static partial int DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial int RegisterHotKey(nint window, int id, int modifiers, int key);
    [LibraryImport("user32.dll")] private static partial int UnregisterHotKey(nint window, int id);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] private static partial int AppendMenu(nint menu, int flags, nint id, string? text);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenu(nint menu, int flags, int x, int y, int reserved, nint window, nint rect);
    [LibraryImport("user32.dll")] private static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] private static partial int GetCursorPos(out Point point);
    [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint window);
}
