using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

/// <summary>展示本地和上游工具版本，处理选择、安装和删除操作。</summary>
public sealed partial class ToolVersionsDialog : ContentDialog
{
    private readonly ToolUpdater _updater;
    private readonly Action<MediaTool, ToolInstallation> _changed;
    private readonly Func<Task> _stopPreview;
    private readonly int _logWindowId;
    private readonly HashSet<string> _releaseTags = new(StringComparer.Ordinal);
    private CancellationTokenSource? _task;
    private int? _nextPage;
    private bool _usingWebList;
    private bool _closeRequested;

    private MediaTool Tool => ToolBox.SelectedIndex == 1 ? MediaTool.YtDlp : MediaTool.Ffmpeg;
    private string ToolName => Tool == MediaTool.Ffmpeg ? "FFmpeg" : "yt-dlp";
    private VersionRow? Selection => VersionList.SelectedItem as VersionRow;

    /// <summary>表示工具版本列表中的显示项。</summary>
    /// <param name="Tag">工具版本的标签。</param>
    /// <param name="InUse">是否为程序当前使用的版本。</param>
    /// <param name="IsUsable">该版本的程序文件是否可用。</param>
    public sealed record VersionRow(string Tag, bool InUse, bool IsUsable)
    {
        /// <summary>含活动或不可用状态的显示文本。</summary>
        public string Label => InUse ? $"{Tag}  ·  使用中" : IsUsable ? Tag : $"{Tag}  ·  不可用";
    }

    /// <summary>初始化窗口使用的工具版本管理对话框。</summary>
    /// <param name="updater">读取、安装和删除工具版本的管理器。</param>
    /// <param name="changed">将本地版本快照同步给宿主窗口的回调。</param>
    /// <param name="stopPreview">等待预览释放媒体工具的回调。</param>
    /// <param name="logWindowId">用于关联日志的窗口编号。</param>
    internal ToolVersionsDialog(ToolUpdater updater, Action<MediaTool, ToolInstallation> changed, Func<Task> stopPreview, int logWindowId)
    {
        _updater = updater;
        _changed = changed;
        _stopPreview = stopPreview;
        _logWindowId = logWindowId;
        InitializeComponent();
        ToolBox.SelectedIndex = 0;
    }

    /// <summary>重新读取当前工具的本地版本和活动路径。</summary>
    /// <param name="selectTag">优先选中的本地版本标签。为空时选中活动版本。</param>
    /// <remarks>可安装列表仅使用对话框中已取得的发行数据。</remarks>
    private void RefreshVersions(string? selectTag = null)
    {
        ToolInstallation installed = _updater.GetInstallation(Tool);
        VersionRow[] versions = installed.Versions
            .Select(row => new VersionRow(row.Tag, row.InUse, row.IsUsable)).ToArray();
        VersionRow? active = versions.FirstOrDefault(version => version.InUse);
        ActiveVersion.Text = active is not null ? $"程序使用版本 {active.Tag}" : versions.Length == 0 ? "尚未安装" : "没有可用版本";
        VersionList.ItemsSource = versions;
        VersionList.SelectedItem = versions.FirstOrDefault(version => version.Tag == selectTag) ?? active;
        RefreshAvailableVersions();
        _changed(Tool, installed);
    }

    // 从可安装列表中排除有效的本地版本，保留损坏版本的重装入口。
    private void RefreshAvailableVersions()
    {
        string? selectedTag = AvailableVersions.SelectedItem as string;
        var installed = VersionList.Items.OfType<VersionRow>().Where(version => version.IsUsable)
            .Select(version => version.Tag).ToHashSet(StringComparer.Ordinal);
        string[] choices = _releaseTags.Where(tag => !installed.Contains(tag)).OrderByDescending(Version.Parse).ToArray();
        AvailableVersions.ItemsSource = choices;
        AvailableVersions.SelectedItem = choices.Contains(selectedTag) ? selectedTag : choices.FirstOrDefault();
        UpdateButtons();
    }

    /// <summary>切换工具时重置上游列表和分页，并显示所选工具的本地版本。</summary>
    /// <param name="sender">触发选择变更的工具下拉框。</param>
    /// <param name="e">工具选择变更的事件数据。</param>
    /// <remarks>上游版本由用户点击「刷新」后获取。</remarks>
    private void Tool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ToolBox.SelectedIndex < 0 || AvailableVersions is null)
            return;
        _releaseTags.Clear();
        _nextPage = null;
        _usingWebList = false;
        MessageBar.IsOpen = false;
        RefreshVersions();
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void More_Click(object sender, RoutedEventArgs e) => await LoadReleasePageAsync(reset: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadReleasePageAsync(reset: true);

    /// <summary>响应用户操作，查询当前工具的一页上游版本。</summary>
    /// <param name="reset">为 <see langword="true" /> 时从第 1 页重新查询，为 <see langword="false" /> 时沿用上一次的来源和分页位置。</param>
    /// <returns>查询完成的任务。尚无下一页时直接完成。</returns>
    private async Task LoadReleasePageAsync(bool reset)
    {
        int? page = reset ? 1 : _nextPage;
        if (page is null)
            return;
        await RunAsync(null, async token =>
        {
            InstallProgress.Visibility = Visibility.Visible;
            InstallProgress.IsIndeterminate = true;
            ShowMessage(InfoBarSeverity.Informational, $"正在获取 {ToolName} 版本列表…");
            ToolReleasePage releases = await _updater.GetReleasePageAsync(Tool, page.Value, !reset && _usingWebList, token,
                () => ReportWebFallback(token));
            if (reset)
                _releaseTags.Clear();
            _releaseTags.UnionWith(releases.Tags);
            _nextPage = releases.NextPage;
            _usingWebList = releases.UsedWebFallback;
            RefreshAvailableVersions();
            string source = releases.UsedWebFallback ? "GitHub API 查询失败（HTTP 403），已从官方发布页读取列表。\n" : "";
            string count = AvailableVersions.Items.Count == 0 ? "暂无可安装版本。" :
                $"可选 {AvailableVersions.Items.Count} 个版本。";
            return source + count;
        }, changesTools: false);
    }

    private void UpdateButtons()
    {
        if (RefreshButton is null)
            return;
        bool available = _task is null;
        ToolBox.IsEnabled = available;
        VersionList.IsEnabled = available;
        AvailableVersions.IsEnabled = available && AvailableVersions.Items.Count > 0;
        InstallButton.IsEnabled = available && AvailableVersions.SelectedItem is string;
        MoreButton.IsEnabled = available && _nextPage is not null;
        RefreshButton.IsEnabled = available;
        UseButton.IsEnabled = available && Selection is { IsUsable: true };
        DeleteButton.IsEnabled = available && Selection is not null;
        AutoButton.IsEnabled = available && VersionList.Items.OfType<VersionRow>().Any(version => version.IsUsable);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (AvailableVersions.SelectedItem is not string tag)
            return;
        await RunAsync(tag, async token =>
        {
            InstallProgress.Visibility = Visibility.Visible;
            InstallProgress.IsIndeterminate = true;
            ShowMessage(InfoBarSeverity.Informational, $"正在查询 {ToolName} {tag}…");
            var progress = new Progress<MediaProgress>(value =>
            {
                if (_task?.Token != token || value.Bytes is not long bytes || value.TotalBytes is not > 0)
                    return;
                InstallProgress.IsIndeterminate = false;
                InstallProgress.Value = Math.Clamp(bytes * 100d / value.TotalBytes.Value, 0, 100);
            });
            ToolUpdateResult result = await _updater.InstallVersionAsync(Tool, tag, progress, token,
                () => ReportWebFallback(token));
            string source = result.UsedWebFallback ? "GitHub API 查询失败（HTTP 403），已使用官方发布页查询。\n" : "";
            return $"{source}{ToolName} {tag} {(result.Updated ? "已安装" : "已在本地安装")}。";
        });
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { IsUsable: true } version)
            return;
        await RunAsync(version.Tag, async token =>
        {
            await _updater.SelectVersionAsync(Tool, version.Tag, token);
            return $"后续任务将使用 {ToolName} {version.Tag}。";
        });
    }

    private async void Auto_Click(object sender, RoutedEventArgs e) => await RunAsync(null, _ =>
    {
        _updater.SelectLatest(Tool);
        return Task.FromResult("程序将自动使用本地可用的最高版本。");
    });

    private void Delete_Opening(object sender, object e)
    {
        if (Selection is not { } version)
            return;
        string consequence = !version.InUse ? "" : !VersionList.Items.OfType<VersionRow>().Any(row => row.Tag != version.Tag && row.IsUsable)
            ? "删除后需重新安装才能使用该工具。"
            : "程序将自动选择剩余的最新版本。";
        DeleteMessage.Text = $"删除 {ToolName} {version.Tag}？{consequence}";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        if (Selection is not { } version)
            return;
        await RunAsync(null, async token =>
        {
            // 先关闭预览，释放可能仍被播放进程占用的工具文件。
            await _stopPreview();
            token.ThrowIfCancellationRequested();
            _updater.DeleteVersion(Tool, version.Tag);
            return $"已删除 {ToolName} {version.Tag}。";
        });
    }

    private async Task RunAsync(string? selectTag, Func<CancellationToken, Task<string>> action, bool changesTools = true)
    {
        if (_task is not null)
            return;
        using var task = new CancellationTokenSource();
        _task = task;
        UpdateButtons();
        MessageBar.IsOpen = false;
        try
        {
            string message = await action(task.Token);
            if (changesTools)
                RefreshVersions(selectTag);
            ShowMessage(changesTools ? InfoBarSeverity.Success : InfoBarSeverity.Informational, message);
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested) { }
        catch (OperationCanceledException) { ShowMessage(InfoBarSeverity.Error, "操作超时，请重试。"); }
        catch (Exception ex)
        {
            PortableStorage.Log.Write(LogLevel.Debug, "ToolVersions", $"处理 {ToolName} 版本时发生异常。", ex, _logWindowId);
            ShowMessage(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            _task = null;
            InstallProgress.Visibility = Visibility.Collapsed;
            UpdateButtons();
            if (_closeRequested)
                Hide();
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
        LogLevel level = severity switch
        {
            InfoBarSeverity.Error => LogLevel.Error,
            InfoBarSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information
        };
        PortableStorage.Log.Write(level, "ToolVersions", message, error: null, windowId: _logWindowId);
    }

    private void ReportWebFallback(CancellationToken token) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_task?.Token == token)
            ShowMessage(InfoBarSeverity.Warning, "GitHub API 查询失败（HTTP 403），正在使用官方发布页查询。");
    });

    // 先请求取消正在执行的任务，让安装或删除流程完成清理后再关闭。
    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_task is null)
            return;
        args.Cancel = true;
        CancelAndClose();
    }

    /// <summary>请求关闭版本对话框。</summary>
    /// <remarks>如果有任务在执行，先发送取消请求，由任务清理完成后关闭。</remarks>
    /// <summary>请求取消当前工具任务，并在清理完成后关闭对话框。</summary>
    internal void CancelAndClose()
    {
        _closeRequested = true;
        if (_task is { } task)
            task.Cancel();
        else
            Hide();
    }
}
