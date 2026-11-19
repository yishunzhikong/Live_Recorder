using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace LiveRecorder;

/// <summary>按显示器工作区布置窗口，并用固定的客户区比例处理原生边框拖动。</summary>
internal sealed class WindowPlacement
{
    internal const double LayoutWidth = 1280;
    internal const double LayoutHeight = 800;

    private const uint WmSizing = 0x0214;
    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;
    private const uint WmNcDestroy = 0x0082;
    private readonly AppWindow _window;
    private readonly nint _handle;
    // 原生回调在窗口销毁前须保持存活。
    private readonly SubclassProc _callback;
    private RectInt32 _workArea;
    private SizeInt32? _sizingFrame;
    private double _relativeScale = 1;

    /// <summary>按工作区居中打开窗口，并注册比例缩放和跨屏移动处理。</summary>
    /// <param name="window">应用的窗口对象。</param>
    /// <param name="handle">同一窗口的原生句柄。</param>
    public WindowPlacement(AppWindow window, nint handle)
    {
        _window = window;
        _handle = handle;
        _callback = OnWindowMessage;
        _workArea = GetWorkArea();
        var frame = GetFrameSize();
        var size = CalculateSize(DefaultScale(_workArea, frame), frame);
        SetMinimum(frame);
        window.MoveAndResize(new RectInt32(_workArea.X + (_workArea.Width - size.Width) / 2,
            _workArea.Y + (_workArea.Height - size.Height) / 2, size.Width, size.Height));
        IsResizeConstrained = SetWindowSubclass(handle, _callback, 1, 0);
    }

    /// <summary>原生边框拖动处理是否已注册。</summary>
    public bool IsResizeConstrained { get; }

    private RectInt32 GetWorkArea() => DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Primary).WorkArea;

    /// <summary>按工作区宽度和高度的占比计算默认客户区缩放。</summary>
    /// <param name="area">显示器扣除任务栏后的工作区。</param>
    /// <param name="frame">窗口边框和标题栏占用的像素尺寸。</param>
    /// <returns>相对于逻辑布局的缩放倍数。</returns>
    internal static double DefaultScale(RectInt32 area, SizeInt32 frame) =>
        Math.Max(0.01, Math.Min((area.Width * (2d / 3d) - frame.Width) / LayoutWidth,
            (area.Height * 0.8 - frame.Height) / LayoutHeight));

    private static SizeInt32 CalculateSize(double scale, SizeInt32 frame) =>
        new((int)Math.Round(LayoutWidth * scale) + frame.Width, (int)Math.Round(LayoutHeight * scale) + frame.Height);

    private SizeInt32 GetFrameSize()
    {
        if (!GetWindowRect(_handle, out WindowRect outer) || !GetClientRect(_handle, out WindowRect client))
            return default;
        return new(Math.Max(0, outer.Width - client.Width), Math.Max(0, outer.Height - client.Height));
    }

    private double MinimumScale(SizeInt32 frame) => DefaultScale(_workArea, frame) * 0.65;

    private double MaximumScale(SizeInt32 frame) =>
        Math.Max(MinimumScale(frame), Math.Min((_workArea.Width - frame.Width) / LayoutWidth,
            (_workArea.Height - frame.Height) / LayoutHeight));

    private void SetMinimum(SizeInt32 frame)
    {
        if (_window.Presenter is OverlappedPresenter presenter)
        {
            var size = CalculateSize(MinimumScale(frame), frame);
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = size.Width;
            presenter.PreferredMinimumHeight = size.Height;
        }
    }

    private nint OnWindowMessage(nint handle, uint message, nuint edge, nint parameter, nuint id, nuint data)
    {
        if (message == WmSizing && parameter != 0 && edge is >= 1 and <= 8)
        {
            var rect = Marshal.PtrToStructure<WindowRect>(parameter);
            var frame = _sizingFrame ?? GetFrameSize();
            int width = rect.Width - frame.Width;
            int height = rect.Height - frame.Height;
            double scale = edge switch
            {
                1 or 2 => width / LayoutWidth,
                3 or 6 => height / LayoutHeight,
                // 将角点投影到比例线，避免斜拖时在横纵两个尺寸基准间反复切换。
                _ => (width * LayoutWidth + height * LayoutHeight) / (LayoutWidth * LayoutWidth + LayoutHeight * LayoutHeight)
            };
            scale = Math.Clamp(scale, MinimumScale(frame), MaximumScale(frame));
            _relativeScale = scale / DefaultScale(_workArea, frame);
            var size = CalculateSize(scale, frame);

            // 拖边时保持对边不动，另一轴以中点缩放。拖角时固定对角。
            int left = edge switch
            {
                1 or 4 or 7 => rect.Right - size.Width,
                3 or 6 => rect.Left + (rect.Width - size.Width) / 2,
                _ => rect.Left
            };
            int top = edge switch
            {
                3 or 4 or 5 => rect.Bottom - size.Height,
                1 or 2 => rect.Top + (rect.Height - size.Height) / 2,
                _ => rect.Top
            };
            rect.Left = left;
            rect.Top = top;
            rect.Right = left + size.Width;
            rect.Bottom = top + size.Height;
            Marshal.StructureToPtr(rect, parameter, false);
            return 1;
        }

        if (message == WmEnterSizeMove)
        {
            var frame = GetFrameSize();
            // 一次拖动复用同一组边框像素，跨屏后再按新工作区和新 DPI 计算。
            _sizingFrame = frame;
            if (GetClientRect(handle, out WindowRect client))
                _relativeScale = client.Width / LayoutWidth / DefaultScale(_workArea, frame);
        }
        else if (message == WmNcDestroy)
            RemoveWindowSubclass(handle, _callback, id);

        nint result = DefSubclassProc(handle, message, edge, parameter);
        if (message == WmExitSizeMove)
        {
            _sizingFrame = null;
            UpdateDisplay();
        }
        return result;
    }

    private void UpdateDisplay()
    {
        RectInt32 area = GetWorkArea();
        if (area.Equals(_workArea))
            return;
        _workArea = area;
        var frame = GetFrameSize();
        SetMinimum(frame);
        double scale = Math.Clamp(_relativeScale * DefaultScale(area, frame), MinimumScale(frame), MaximumScale(frame));
        var size = CalculateSize(scale, frame);
        var position = _window.Position;
        int x = Math.Clamp(position.X, area.X, area.X + Math.Max(0, area.Width - size.Width));
        int y = Math.Clamp(position.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height));
        _window.MoveAndResize(new RectInt32(x, y, size.Width, size.Height));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetClientRect(nint window, out WindowRect rect);
}
