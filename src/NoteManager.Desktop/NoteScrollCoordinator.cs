using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace NoteManager.Desktop;

internal enum ScrollDeltaMode
{
    Pixel,
    Line,
    Page
}

internal static class NoteScrollCoordinator
{
    private const double LineScrollDistance = 48;

    public static bool IsModifiedGesture(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Control)
           || modifiers.HasFlag(KeyModifiers.Meta)
           || modifiers.HasFlag(KeyModifiers.Alt)
           || modifiers.HasFlag(KeyModifiers.Shift);

    public static bool ScrollBy(
        ScrollViewer scrollViewer,
        double delta,
        ScrollDeltaMode mode)
    {
        var maximumOffset = Math.Max(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if (maximumOffset <= 0 || !double.IsFinite(delta) || delta == 0)
        {
            return false;
        }

        var distance = mode switch
        {
            ScrollDeltaMode.Pixel => delta,
            ScrollDeltaMode.Line => delta * LineScrollDistance,
            ScrollDeltaMode.Page => delta * scrollViewer.Viewport.Height,
            _ => 0
        };
        distance = Math.Clamp(
            distance,
            -scrollViewer.Viewport.Height,
            scrollViewer.Viewport.Height);

        var offset = scrollViewer.Offset;
        var target = Math.Clamp(offset.Y + distance, 0, maximumOffset);
        scrollViewer.Offset = new Vector(offset.X, target);
        return true;
    }
}
