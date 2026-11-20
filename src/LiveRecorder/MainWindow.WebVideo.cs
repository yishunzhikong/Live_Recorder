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
    private bool _loadingCookieFile;
    private string? _cookieFileError;

    /// <summary>返回当前启用的 Cookie 来源，关闭时为空。</summary>
    private string? WebCookieSource => WebCookiesCheckBox.IsChecked == true
        ? (WebCookieBrowserBox.SelectedItem as ComboBoxItem)?.Tag as string
        : null;
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

    private void WebSource_TextChanged(object sender, TextChangedEventArgs e) => ResetWebAnalysis();

    /// <summary>切换 Cookie 来源时读取已保存的文件并清除旧解析结果。</summary>
    private void WebCookieSourceChanged()
    {
        if (WebCookieSource == "file")
        {
            _loadingCookieFile = true;
            try
            {
                WebCookieFileBox.Text = File.ReadAllText(PortableStorage.CookieFile);
                SetCookieFileError(null);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                WebCookieFileBox.Text = "";
                SetCookieFileError(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetCookieFileError("无法读取 config/cookies.txt。请检查程序目录的访问权限。");
            }
            finally { _loadingCookieFile = false; }
        }
        ResetWebAnalysis();
    }

    /// <summary>编辑 Cookie 时保存文本，同时使旧的媒体链接失效。</summary>
    /// <param name="sender">Cookie 编辑框。</param>
    /// <param name="e">文本变更事件。</param>
    private void WebCookieFile_TextChanging(TextBox sender, TextBoxTextChangingEventArgs e)
    {
        if (_loadingCookieFile || WebCookieSource != "file")
            return;
        try
        {
            PortableStorage.SaveCookieFile(sender.Text);
            SetCookieFileError(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCookieFileError("无法保存 config/cookies.txt。请检查程序目录的写入权限。");
        }
        ResetWebAnalysis();
    }

    /// <summary>在编辑框下方显示 Cookie 文件错误。</summary>
    /// <param name="message">错误提示。为空时隐藏提示。</param>
    private void SetCookieFileError(string? message)
    {
        _cookieFileError = message;
        WebCookieFileStatus.Text = message ?? "";
        WebCookieFileStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>在网页地址或 Cookie 选项变化后清空旧的解析结果。</summary>
    private void ResetWebAnalysis()
    {
        if (WebFormatBox is null || WebLinkBox is null || MediaPane is null)
            return;
        WebFormatBox.ItemsSource = null;
        WebLinkBox.Text = "";
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
            string? cookieSource = WebCookieSource;
            bool useCookieFile = cookieSource == "file";
            if (useCookieFile && (_cookieFileError is not null || string.IsNullOrWhiteSpace(WebCookieFileBox.Text)))
                throw new InvalidOperationException(_cookieFileError ?? "请先粘贴 Netscape 格式的 cookies.txt 内容。");
            WebVideoAnalysis result = await _webAnalyzer.AnalyzeAsync(WebSourceBox.Text, token,
                cookieBrowser: useCookieFile ? null : cookieSource,
                cookieFile: useCookieFile ? PortableStorage.CookieFile : null);
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
        bool useCookies = WebCookiesCheckBox.IsChecked == true;
        WebCookieBrowserBox.Visibility = useCookies ? Visibility.Visible : Visibility.Collapsed;
        WebCookieBrowserBox.IsEnabled = enabled && useCookies;
        WebCookieFileSettings.Visibility = WebCookieSource == "file" ? Visibility.Visible : Visibility.Collapsed;
        WebCookieFileBox.IsEnabled = enabled;
        WebFormatBox.IsEnabled = enabled && WebFormatBox.Items.Count > 0;
    }

}
