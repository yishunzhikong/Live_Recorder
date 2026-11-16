using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace LiveRecorder;

internal static class WindowAspect
{
    private const uint WmSizing = 0x0214;
    private const uint WmNcDestroy = 0x0082;
    private static readonly SubclassProc Resize = OnWindowMessage;

    public static void Apply(AppWindow window, nint handle)
    {
        double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96.0;
        RectInt32 workArea = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary).WorkArea;
        int unit = Math.Min((int)Math.Round(80 * scale), Math.Min(workArea.Width / 16, workArea.Height / 9));
        int minimumUnit = Math.Min(unit, (int)Math.Ceiling(60 * scale));
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = 16 * minimumUnit;
            presenter.PreferredMinimumHeight = 9 * minimumUnit;
        }
        window.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - 16 * unit) / 2,
            workArea.Y + (workArea.Height - 9 * unit) / 2, 16 * unit, 9 * unit));
        SetWindowSubclass(handle, Resize, 1, (nuint)minimumUnit);
    }

    private static nint OnWindowMessage(nint handle, uint message, nuint edge, nint parameter, nuint id, nuint minimumUnit)
    {
        if (message == WmNcDestroy)
            RemoveWindowSubclass(handle, Resize, id);
        if (message != WmSizing)
            return DefSubclassProc(handle, message, edge, parameter);

        var rect = Marshal.PtrToStructure<WindowRect>(parameter);
        GetWindowRect(handle, out WindowRect current);
        double widthUnits = (rect.Right - rect.Left) / 16d;
        double heightUnits = (rect.Bottom - rect.Top) / 9d;
        double widthChange = Math.Abs(widthUnits - (current.Right - current.Left) / 16d);
        double heightChange = Math.Abs(heightUnits - (current.Bottom - current.Top) / 9d);
        double units = edge switch
        {
            1 or 2 => widthUnits,
            3 or 6 => heightUnits,
            _ => widthChange >= heightChange ? widthUnits : heightUnits
        };
        int unit = Math.Max((int)minimumUnit, (int)Math.Round(units));
        if (edge is 1 or 4 or 7)
            rect.Left = rect.Right - 16 * unit;
        else
            rect.Right = rect.Left + 16 * unit;
        if (edge is 3 or 4 or 5)
            rect.Top = rect.Bottom - 9 * unit;
        else
            rect.Bottom = rect.Top + 9 * unit;
        Marshal.StructureToPtr(rect, parameter, false);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);
}
