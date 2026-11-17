using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private enum AppMode { Live, WebVideo }

    private WebVideoAnalyzer _webAnalyzer;
    private AppMode _mode;
    private readonly (string Title, string Detail)[] _modeStatus =
    [
        ("就绪", "粘贴媒体链接，或选择要处理的本地文件。"),
        ("就绪", "在左侧输入网页视频链接，分析后可使用右侧预览。")
    ];

    private async void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0 || (AppMode)ModeBox.SelectedIndex == _mode)
            return;

        _modeStatus[(int)_mode] = (StatusTitle.Text, StatusDetail.Text);
        _mode = (AppMode)ModeBox.SelectedIndex;
        SourceBox.Visibility = _mode == AppMode.Live ? Visibility.Visible : Visibility.Collapsed;
        ReconnectSettingsPanel.Visibility = SourceBox.Visibility;
        WebSettings.Visibility = _mode == AppMode.WebVideo ? Visibility.Visible : Visibility.Collapsed;
        var status = _modeStatus[(int)_mode];
        SetStatus(status.Title, status.Detail, LogLevel.Debug);

        Task stopped = StopPreviewAsync();
        PreviewMessage.Text = PreviewIdleMessage;
        await stopped;
    }

    private Uri SelectedMediaSource() => _mode switch
    {
        AppMode.Live => MediaService.ParseSource(SourceBox.Text),
        AppMode.WebVideo when SelectedWebFormat is { } link => MediaService.ParseSource(link.Url),
        AppMode.WebVideo => throw new ArgumentException("请先在左侧分析网页链接并选择媒体格式。"),
        _ => throw new InvalidOperationException("未知的模式。")
    };

    private WebVideoLink? SelectedWebFormat => _mode == AppMode.WebVideo ? WebFormatBox.SelectedItem as WebVideoLink : null;

    private WebVideoLink? SelectedWebAudio => SelectedWebFormat is { AudioOnly: true } audio ? audio : null;

    private async Task<string> DownloadWebAudioAsync(WebVideoLink audio, string directory, CancellationToken token)
    {
        DirectoryInfo work = Directory.CreateDirectory(Path.Combine(PortableStorage.CacheDirectory, $"audio-{Guid.NewGuid():N}"));
        bool preserveDownload = false;
        try
        {
            SetStatus("正在下载音频", "yt-dlp 正在下载所选音轨并准备 M4A 文件。");
            string file = await _webAnalyzer.DownloadAudioAsync(audio, work.FullName, _ffmpegPath, token);
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => MediaService.SaveDownloadedAudio(file, directory, DateTime.Now), token);
        }
        catch (MediaPublishException) { preserveDownload = true; throw; }
        finally { if (!preserveDownload) DeleteWebAudioFiles(work); }
    }

    private static void DeleteWebAudioFiles(DirectoryInfo folder)
    {
        try { folder.Delete(recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PortableStorage.Log.Write(LogLevel.Warning, "Download", "无法清理音频缓存。", ex);
        }
    }

    private string PreviewIdleMessage => _mode == AppMode.WebVideo
        ? "在左侧选择解析出的媒体直链，或打开本地视频。"
        : PreviewIdle;

    private void WebSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (WebFormatBox is null)
            return;
        WebFormatBox.ItemsSource = null;
        WebVideoTitle.Text = "";
        UpdateWebActions(_operation is null);
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
            SetStatus("分析完成", $"找到 {result.Links.Count} 条媒体直链。点击右侧「预览链接」播放所选格式。");
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
        catch (Exception ex) { SetStatus("无法复制直链", ex.Message, LogLevel.Error, ex); }
    }
}
