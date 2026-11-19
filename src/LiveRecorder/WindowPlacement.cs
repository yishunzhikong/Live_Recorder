using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace LiveRecorder;

/// <summary>设置主窗口的初始尺寸和独立缩放的最小尺寸。</summary>
internal static class WindowPlacement
{
    /// <summary>在当前显示器工作区中居中打开主窗口。</summary>
    /// <param name="window">需要设置位置和初始尺寸的应用窗口。</param>
    /// <param name="handle">同一窗口的原生句柄，用于读取 DPI。</param>
    public static void Apply(AppWindow window, nint handle)
    {
        double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96.0;
        RectInt32 workArea = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(workArea.Width, (int)Math.Round(1280 * scale));
        // 按右列宽度估算 16:9 画面高度，再给标题栏、菜单、播放控件和操作区留出空间。
        double previewHeight = width * (2d / 3d) * (9d / 16d);
        int height = Math.Min(workArea.Height, (int)Math.Round(previewHeight + 349 * scale));
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = Math.Min(width, (int)Math.Ceiling(960 * scale));
            presenter.PreferredMinimumHeight = Math.Min(height, (int)Math.Ceiling(640 * scale));
        }
        window.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
