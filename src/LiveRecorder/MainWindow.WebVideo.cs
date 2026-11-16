using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private enum AppMode { Live, WebVideo, Cctv }

    private readonly WebVideoAnalyzer _webAnalyzer = new(
        Path.Combine(AppContext.BaseDirectory, "yt-dlp", "yt-dlp.exe"),
        Path.Combine(AppContext.BaseDirectory, "yt-dlp", "deno.exe"));
    private AppMode _mode;
    private (string Title, string Detail) _liveStatus = ("就绪", "粘贴媒体链接，或选择要处理的本地文件。");
    private (string Title, string Detail) _webStatus = ("就绪", "粘贴网页视频链接，然后点击「分析链接」。");

    private async void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0 || (AppMode)ModeBox.SelectedIndex == _mode)
            return;

        if (_mode == AppMode.Live)
            _liveStatus = (StatusTitle.Text, StatusDetail.Text);
        else if (_mode == AppMode.WebVideo)
            _webStatus = (StatusTitle.Text, StatusDetail.Text);
        _mode = (AppMode)ModeBox.SelectedIndex;
        bool live = _mode == AppMode.Live;
        bool web = _mode == AppMode.WebVideo;
        LiveSettings.Visibility = LivePreviewCard.Visibility = LiveActions.Visibility = PlayResultButton.Visibility =
            live ? Visibility.Visible : Visibility.Collapsed;
        WebSourceBox.Visibility = WebResultsCard.Visibility = WebActions.Visibility =
            web ? Visibility.Visible : Visibility.Collapsed;
        ActionsCard.Visibility = live || web ? Visibility.Visible : Visibility.Collapsed;
        CctvCard.Visibility = _mode == AppMode.Cctv ? Visibility.Visible : Visibility.Collapsed;
        if (live || web)
        {
            var status = live ? _liveStatus : _webStatus;
            SetStatus(status.Title, status.Detail);
        }
        if (!live)
            await StopPreviewAsync();
    }

    private async void AnalyzeWeb_Click(object sender, RoutedEventArgs e) => await RunAsync("分析中", async token =>
    {
        WebFormatBox.ItemsSource = null;
        WebLinkBox.Text = "";
        WebVideoTitle.Text = "正在分析网页…";
        StatusDetail.Text = "正在识别网页中的视频格式…";
        try
        {
            WebVideoAnalysis result = await _webAnalyzer.AnalyzeAsync(WebSourceBox.Text, token);
            WebVideoTitle.Text = result.Title;
            WebFormatBox.ItemsSource = result.Links;
            WebFormatBox.SelectedIndex = 0;
            SetStatus("分析完成", $"找到 {result.Links.Count} 条媒体直链。");
        }
        catch
        {
            WebVideoTitle.Text = "分析网页后可在这里选择并复制媒体直链。";
            throw;
        }
    });

    private void WebFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        WebLinkBox.Text = (WebFormatBox.SelectedItem as WebVideoLink)?.Url ?? "";
        UpdateWebActions(_operation is null);
    }

    private void UpdateWebActions(bool enabled)
    {
        WebFormatBox.IsEnabled = enabled && WebFormatBox.Items.Count > 0;
        CopyWebLinkButton.IsEnabled = enabled && WebFormatBox.SelectedItem is WebVideoLink;
    }

    private void CopyWebLink_Click(object sender, RoutedEventArgs e)
    {
        if (WebFormatBox.SelectedItem is not WebVideoLink link)
            return;
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(link.Url);
            Clipboard.SetContent(data);
            SetStatus("已复制直链", "可粘贴到其他播放器或下载工具。");
        }
        catch (Exception ex) { SetStatus("无法复制直链", ex.Message); }
    }
}
