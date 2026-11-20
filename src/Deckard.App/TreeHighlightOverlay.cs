using System.Runtime.InteropServices;
using Deckard.Core.Capture;
using Microsoft.UI.Dispatching;

namespace Deckard.App;

/// <summary>
/// A build node on the in-game skill tree or paragon screen, in captured-frame pixels; Rank 0 shows no badge. A ghost is
/// drawn thin and pale (a paragon board still to attach); a label is written beside the mark.
/// </summary>
public readonly record struct TreeMark(double X, double Y, bool IsSkill, int Rank, bool Ghost = false, string? Label = null);

/// <summary>What to highlight over the open in-game skill tree or paragon screen.</summary>
/// <param name="Panel">The tree panel in frame pixels; marks outside it are clipped like the game clips the tree.</param>
/// <param name="Zoom">The in-game tree zoom; node frames and badges scale with it.</param>
public sealed record TreeHighlight(nint GameWindow, int FrameHeight, PixelRect Panel, IReadOnlyList<TreeMark> Marks, double Zoom);

/// <summary>
/// Gold frames and target-rank badges over the build's nodes on the in-game skill tree (on the paragon screen: frames,
/// the next board's pale outline and labels; one setting, HighlightSkillTree, turns both off). A plain Win32 layered window
/// (per-pixel alpha, drawn with GDI, pushed with UpdateLayeredWindow) covering only the tree panel: click-through,
/// topmost, never activated, nothing hooked into the game. Updates are coalesced so it always draws the newest
/// placement and never queues behind a fast drag.
/// </summary>
internal sealed partial class TreeHighlightOverlay : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly OverlayVisibility _visibility;
    private readonly object _gate = new();
    private TreeHighlight? _pending;
    private TreeHighlight? _current; // last placement, drawn again when the overlay becomes visible
    private bool _queued;
    private nint _hwnd, _memDc, _bitmap, _bits;
    private int _width, _height, _scaleFor;
    private nint _pen, _brush, _font, _ghostPen, _labelBrush;

    public TreeHighlightOverlay(DispatcherQueue ui, OverlayVisibility visibility)
    {
        _ui = ui;
        _visibility = visibility;
        visibility.Changed += _ => Show(_current);
        AppSettings.Shared.Changed += () => Show(_current);
    }

    private void Show(TreeHighlight? highlight)
    {
        _current = highlight;
        if (highlight is null || !_visibility.InGame || !AppSettings.Shared.HighlightSkillTree) // the screen is open on purpose: no overlay key needed
            Hide();
        else
            Draw(highlight);
    }

    /// <summary>Thread-safe; null hides the highlight.</summary>
    public void Post(TreeHighlight? highlight)
    {
        lock (_gate)
        {
            _pending = highlight;
            if (_queued)
                return;
            _queued = true;
        }
        _ui.TryEnqueue(() =>
        {
            TreeHighlight? next;
            lock (_gate)
            {
                next = _pending;
                _queued = false;
            }
            Show(next);
        });
    }

    private void Hide()
    {
        if (_hwnd != 0)
            ShowWindow(_hwnd, 0);
    }

    private unsafe void Draw(TreeHighlight h)
    {
        // Frames are the game's client area (ItemFeed crops off any title bar).
        if (!OverlayWindow.Native.TryGetClientOrigin(h.GameWindow, out var left, out var top))
            return;
        EnsureWindow();
        EnsureSurface(h.Panel.Width, h.Panel.Height, h.FrameHeight);

        new Span<byte>((void*)_bits, _width * _height * 4).Clear();
        SelectObject(_memDc, _pen);
        SelectObject(_memDc, _font);
        SetBkMode(_memDc, 1); // transparent text background
        SetTextColor(_memDc, 0x00081828); // dark brown on gold; pure black would turn transparent below

        double skillHalf = h.FrameHeight * 0.024 * h.Zoom, modifierHalf = h.FrameHeight * 0.0125 * h.Zoom, badge = h.FrameHeight * 0.0095;
        var diamond = stackalloc Point[4];
        foreach (var mark in h.Marks)
        {
            double x = mark.X - h.Panel.X, y = mark.Y - h.Panel.Y;
            if (mark.Ghost && mark.Label is not null)
                continue; // a hint (which board, how to turn it): its label only, drawn below
            var half = mark.IsSkill ? skillHalf : modifierHalf;
            // Only a whole frame: one cut at the panel's edge looked like a frame in the wrong place.
            if (x - half < 0 || y - half < 0 || x + half >= _width || y + half >= _height)
                continue;
            SelectObject(_memDc, mark.Ghost ? _ghostPen : _pen);
            SelectObject(_memDc, GetStockObject(5 /* NULL_BRUSH */));
            if (mark.IsSkill)
            {
                Rectangle(_memDc, (int)(x - half), (int)(y - half), (int)(x + half), (int)(y + half));
            }
            else
            {
                diamond[0] = new((int)x, (int)(y - half));
                diamond[1] = new((int)(x + half), (int)y);
                diamond[2] = new((int)x, (int)(y + half));
                diamond[3] = new((int)(x - half), (int)y);
                Polygon(_memDc, diamond, 4);
            }
            if (mark.Rank <= 0)
                continue; // a paragon node: no rank to show
            // Target rank in a gold badge at the node's top-right corner.
            int bx = (int)(x + half * 0.8), by = (int)(y - half * 0.8), r = (int)badge;
            SelectObject(_memDc, _brush);
            Ellipse(_memDc, bx - r, by - r, bx + r, by + r);
            var text = mark.Rank.ToString();
            var box = new Rect { Left = bx - r, Top = by - r, Right = bx + r, Bottom = by + r };
            fixed (char* chars = text)
                DrawTextW(_memDc, chars, text.Length, ref box, 0x25 /* DT_CENTER | DT_VCENTER | DT_SINGLELINE */);
        }

        // Labels last, over every mark: tan text on a dark box, beside the mark (a ghost board's label centred on it).
        SetTextColor(_memDc, 0x00B4D2E6); // RGB(230, 210, 180)
        foreach (var mark in h.Marks)
        {
            if (mark.Label is not { Length: > 0 } label)
                continue;
            double x = mark.X - h.Panel.X, y = mark.Y - h.Panel.Y;
            // A node's label goes with it; a board's to attach stays at the nearest edge, so it shows zoomed in too.
            if (!mark.Ghost && (x < 0 || y < 0 || x >= _width || y >= _height))
                continue;
            var measured = new Rect();
            fixed (char* chars = label)
            {
                DrawTextW(_memDc, chars, label.Length, ref measured, 0x420 /* DT_CALCRECT | DT_SINGLELINE */);
                // Wider than the panel (advice on the Mercenaries tab): wrapped to the panel's width instead.
                var lineHeight = measured.Bottom - measured.Top;
                var flags = 0x20u; // DT_SINGLELINE
                if (measured.Right - measured.Left is var single && single > _width - lineHeight * 2)
                {
                    // Even lines, not a full one and a word alone ("Scream." on its own).
                    var lines = (int)Math.Ceiling(single / (double)(_width - lineHeight * 2));
                    measured = new Rect { Right = Math.Min(_width - lineHeight * 2, (int)(single / (double)lines * 1.1)) };
                    DrawTextW(_memDc, chars, label.Length, ref measured, 0x410 /* DT_CALCRECT | DT_WORDBREAK */);
                    flags = 0x10; // DT_WORDBREAK
                }
                int w = measured.Right - measured.Left, th = measured.Bottom - measured.Top, pad = Math.Max(3, lineHeight / 4);
                var labelLeft = mark.Ghost ? (int)(x - w / 2.0) : (int)(x + skillHalf + pad * 2);
                var labelTop = (int)(y - th / 2.0);
                labelLeft = Math.Clamp(labelLeft, pad, Math.Max(pad, _width - w - pad));
                labelTop = Math.Clamp(labelTop, pad, Math.Max(pad, _height - th - pad));
                SelectObject(_memDc, _labelBrush);
                SelectObject(_memDc, _ghostPen);
                Rectangle(_memDc, labelLeft - pad, labelTop - pad, labelLeft + w + pad, labelTop + th + pad);
                var text = new Rect { Left = labelLeft, Top = labelTop, Right = labelLeft + w, Bottom = labelTop + th };
                DrawTextW(_memDc, chars, label.Length, ref text, flags);
            }
        }

        // GDI leaves alpha at 0: make every drawn pixel opaque (colors are then valid premultiplied values).
        var pixels = new Span<uint>((void*)_bits, _width * _height);
        for (var i = 0; i < pixels.Length; i++)
            if (pixels[i] != 0)
                pixels[i] |= 0xFF000000;

        var position = new Point(left + h.Panel.X, top + h.Panel.Y);
        var size = new Size(_width, _height);
        var origin = new Point(0, 0);
        var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(_hwnd, 0, ref position, ref size, _memDc, ref origin, 0, ref blend, 2 /* ULW_ALPHA */);
        // Re-assert topmost each time: a borderless game window can end up above us after it is activated.
        SetWindowPos(_hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /* NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW */);
    }

    private unsafe void EnsureWindow()
    {
        if (_hwnd != 0)
            return;
        var instance = GetModuleHandleW(null);
        fixed (char* className = "DeckardTreeHighlight")
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                Instance = instance,
                ClassName = className,
            };
            RegisterClassExW(ref wc);
            const uint exStyle = 0x80000 /* LAYERED */ | 0x20 /* TRANSPARENT */ | 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x8000000 /* NOACTIVATE */;
            _hwnd = CreateWindowExW(exStyle, className, null, 0x80000000 /* WS_POPUP */, 0, 0, 1, 1, 0, 0, instance, 0);
        }
    }

    private unsafe void EnsureSurface(int width, int height, int frameHeight)
    {
        if (width != _width || height != _height)
        {
            if (_bitmap != 0)
                DeleteObject(_bitmap);
            if (_memDc == 0)
                _memDc = CreateCompatibleDC(0);
            var info = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height /* top-down */, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(_memDc, ref info, 0, out _bits, 0, 0);
            SelectObject(_memDc, _bitmap);
            (_width, _height) = (width, height);
        }
        if (frameHeight != _scaleFor)
        {
            foreach (var old in new[] { _pen, _brush, _font, _ghostPen, _labelBrush })
                if (old != 0)
                    DeleteObject(old);
            const uint gold = 0x0060C0F0; // COLORREF 0x00BBGGRR for RGB(240, 192, 96)
            _pen = CreatePen(0, Math.Max(2, frameHeight / 480), gold);
            _brush = CreateSolidBrush(gold);
            _ghostPen = CreatePen(0, Math.Max(1, frameHeight / 960), 0x00E6C88C); // RGB(140, 200, 230)
            _labelBrush = CreateSolidBrush(0x00101418); // RGB(24, 20, 16): not black, which would turn transparent
            fixed (char* face = "Segoe UI")
                _font = CreateFontW(-(int)(frameHeight * 0.0125), 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 5 /* CLEARTYPE */, 0, face);
            _scaleFor = frameHeight;
        }
    }

    public void Dispose()
    {
        foreach (var obj in new[] { _pen, _brush, _font, _ghostPen, _labelBrush, _bitmap })
            if (obj != 0)
                DeleteObject(obj);
        if (_memDc != 0)
            DeleteDC(_memDc);
        if (_hwnd != 0)
            DestroyWindow(_hwnd);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private record struct Size(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSmall;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetProcAddress(nint module, string name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(ref WndClassEx wc);
    [LibraryImport("user32.dll")] private static unsafe partial nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref Point position, ref Size size, nint sourceDc, ref Point sourceOrigin, uint key, ref BlendFunction blend, uint flags);
    [LibraryImport("user32.dll")] private static unsafe partial int DrawTextW(nint dc, char* text, int length, ref Rect rect, uint format);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int index);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] private static unsafe partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Rectangle(nint dc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool Polygon(nint dc, Point* points, int count);
}
