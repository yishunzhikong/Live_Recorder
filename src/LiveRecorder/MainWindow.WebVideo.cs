using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

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

    /// <summary>获取当前模式下共用媒体操作使用的地址。</summary>
    /// <returns>直播输入栏的地址或 yt-dlp 所选格式的直链。</returns>
    /// <exception cref="ArgumentException">地址无效，或尚未分析并选择网页格式。</exception>
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
        // 直接下载的音频先留在缓存。输出失败时保留已完成的文件供恢复。
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

    private void DeleteWebAudioFiles(DirectoryInfo folder)
    {
        try { folder.Delete(recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWindowLog(LogLevel.Warning, "Download", "无法清理音频缓存。", ex);
        }
    }

    private string PreviewIdleMessage => _mode == AppMode.WebVideo
        ? "在左侧选择解析出的媒体直链，或打开本地视频。"
        : PreviewIdle;

    // 网页输入变化后清空旧解析结果，避免套用上一个页面的直链。
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
