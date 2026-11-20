using System.Diagnostics;
using System.Runtime.InteropServices;
using Deckard.Core.Capture;

namespace Deckard.Capture;

/// <summary>
/// Finds game windows without touching the game process: process enumeration uses a system snapshot and
/// window lookup walks top-level windows. Nothing here calls OpenProcess on the game.
/// </summary>
public static partial class WindowLocator
{
    public const string Diablo4ProcessName = "Diablo IV";

    public static nint FindByProcessName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.MainWindowHandle != 0)
                    return process.MainWindowHandle;
            }
        }
        return 0;
    }

    /// <summary>Returns the first visible top-level window whose title contains <paramref name="titleFragment"/> (case-insensitive).</summary>
    public static unsafe nint FindByTitle(string titleFragment)
    {
        var state = new TitleSearch(titleFragment);
        var handle = GCHandle.Alloc(state);
        try
        {
            EnumWindows(&EnumTitleCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return state.Result;
    }

    public static unsafe string GetTitle(nint hwnd)
    {
        const int capacity = 512;
        var buffer = stackalloc char[capacity];
        var length = GetWindowTextW(hwnd, buffer, capacity);
        return new string(buffer, 0, length);
    }

    /// <summary>
    /// The window's client area (the game's own picture, without a title bar or borders) in captured-frame pixels:
    /// window capture covers the extended frame bounds. Null when the window is gone.
    /// </summary>
    public static PixelRect? ClientArea(nint hwnd)
    {
        var origin = new Point();
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var bounds, Marshal.SizeOf<Rect>()) != 0
            || !ClientToScreen(hwnd, ref origin) || !GetClientRect(hwnd, out var client))
            return null;
        return new PixelRect(origin.X - bounds.Left, origin.Y - bounds.Top, client.Right - client.Left, client.Bottom - client.Top);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint hwnd, ref Point point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [UnmanagedCallersOnly]
    private static int EnumTitleCallback(nint hwnd, nint lParam)
    {
        var state = (TitleSearch)GCHandle.FromIntPtr(lParam).Target!;
        if (IsWindowVisible(hwnd) && GetTitle(hwnd).Contains(state.Fragment, StringComparison.OrdinalIgnoreCase))
        {
            state.Result = hwnd;
            return 0;
        }
        return 1;
    }

    private sealed class TitleSearch(string fragment)
    {
        public string Fragment { get; } = fragment;
        public nint Result { get; set; }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint hwnd, char* text, int maxCount);
}
