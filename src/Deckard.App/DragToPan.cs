using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Deckard.App;

/// <summary>
/// Drag with the left mouse button to move a ScrollViewer's content, like the game's skill tree and paragon screens.
/// Handles already-handled events too, so elements with tooltips don't swallow the drag; give the content a
/// transparent background so empty space takes it as well.
/// </summary>
internal sealed class DragToPan
{
    private readonly ScrollViewer _scroll;
    private readonly Action<InputCursor?> _setCursor; // ProtectedCursor belongs to the owning control
    private Point? _from;
    private (double H, double V) _offset;

    public DragToPan(ScrollViewer scroll, Action<InputCursor?> setCursor)
    {
        _scroll = scroll;
        _setCursor = setCursor;
        scroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new PointerEventHandler(OnStart), true);
        scroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerMovedEvent, new PointerEventHandler(OnMove), true);
        scroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, new PointerEventHandler(OnEnd), true);
        scroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnEnd), true);
    }

    private void OnStart(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_scroll);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
            return; // touch and pen already pan through the ScrollViewer
        _from = point.Position;
        _offset = (_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _scroll.CapturePointer(e.Pointer);
        _setCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (_from is not { } from)
            return;
        var now = e.GetCurrentPoint(_scroll).Position;
        _scroll.ChangeView(_offset.H - (now.X - from.X), _offset.V - (now.Y - from.Y), null, true);
    }

    private void OnEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_from is null)
            return;
        _from = null;
        _scroll.ReleasePointerCapture(e.Pointer);
        _setCursor(null);
    }
}
