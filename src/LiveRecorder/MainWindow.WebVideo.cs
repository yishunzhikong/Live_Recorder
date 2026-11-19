using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

// 管理直播和 yt-dlp 模式切换、网页格式列表以及音频下载。
public sealed partial class MainWindow
{
    private enum AppMode { Live, WebVideo }

    private WebVideoAnalyzer _webAnalyzer;
    private AppMode _mode;
    // 两个模式分别保留状态区内容，切换时恢复对应的提示。
    private readonly (string Title, string Detail)[] _modeStatus =
    [
        ("就绪", "粘贴媒体链接，或选择要处理的本地文件。"),
        ("就绪", "在左侧输入网页视频链接，分析后可使用右侧预览。")
    ];

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0 || (AppMode)ModeBox.SelectedIndex == _mode)
            return;

        _modeStatus[(int)_mode] = MediaPane.Status;
        _mode = (AppMode)ModeBox.SelectedIndex;
        SourceBox.Visibility = _mode == AppMode.Live ? Visibility.Visible : Visibility.Collapsed;
        WebSettings.Visibility = _mode == AppMode.WebVideo ? Visibility.Visible : Visibility.Collapsed;
        var status = _modeStatus[(int)_mode];
        MediaPane.SetStatus(status.Title, status.Detail, LogLevel.Debug);

        PublishMediaSource();
    }

    /// <summary>将直播输入或 yt-dlp 所选直链发布给右栏。</summary>
    private void PublishMediaSource()
    {
        if (MediaPane is null || SourceBox is null)
            return;
        WebVideoLink? format = _mode == AppMode.WebVideo ? WebFormatBox.SelectedItem as WebVideoLink : null;
        string idle = _mode == AppMode.Live ? "选择预览链接或打开本地视频。" : "在左侧选择解析出的媒体直链，或打开本地视频。";
        try
        {
            string address = _mode == AppMode.Live ? SourceBox.Text :
                format?.Url ?? throw new ArgumentException("请先在左侧分析网页链接并选择媒体格式。");
            MediaPane.SetSource(new(MediaService.ParseSource(address), format), "", idle);
        }
        catch (ArgumentException ex)
        {
            MediaPane.SetSource(null, ex.Message, idle);
        }
    }

    // 网页输入变化后清空旧解析结果，避免套用上一个页面的直链。
    private void WebSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (WebFormatBox is null || MediaPane is null)
            return;
        WebFormatBox.ItemsSource = null;
        WebVideoTitle.Text = "";
        UpdateWebActions(!MediaPane.IsBusy);
        PublishMediaSource();
    }

    private async void AnalyzeWeb_Click(object sender, RoutedEventArgs e) => await MediaPane.RunAsync("分析中", async token =>
    {
        WebFormatBox.ItemsSource = null;
        WebLinkBox.Text = "";
        WebVideoTitle.Text = "正在分析网页…";
        MediaPane.SetStatus("分析中", "正在识别网页中的视频格式…", LogLevel.Debug);
        try
        {
            WebVideoAnalysis result = await _webAnalyzer.AnalyzeAsync(WebSourceBox.Text, token);
            WebVideoTitle.Text = result.Title;
            WebFormatBox.ItemsSource = result.Links;
            WebFormatBox.SelectedIndex = 0;
            MediaPane.SetStatus("分析完成", $"找到 {result.Links.Count} 条媒体直链。点击右侧「预览链接」播放所选格式。");
        }
        catch
        {
            WebVideoTitle.Text = "分析网页后可在这里选择并复制媒体直链。";
            throw;
        }
    });

    private void WebFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaPane is null || WebLinkBox is null)
            return;
        WebLinkBox.Text = (WebFormatBox.SelectedItem as WebVideoLink)?.Url ?? "";
        UpdateWebActions(!MediaPane.IsBusy);
        PublishMediaSource();
    }

    private void UpdateWebActions(bool enabled)
    {
        WebFormatBox.IsEnabled = enabled && WebFormatBox.Items.Count > 0;
    }

}
