using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

public sealed partial class ToolVersionsDialog : ContentDialog
{
    private readonly ToolUpdater _updater;
    private readonly Action _changed;
    private readonly Func<Task> _stopPreview;
    private readonly HashSet<string> _releaseTags = new(StringComparer.Ordinal);
    private CancellationTokenSource? _task;
    private int? _nextPage;
    private bool _usingWebList;
    private bool _loaded;
    private bool _closeRequested;

    private MediaTool Tool => ToolBox.SelectedIndex == 1 ? MediaTool.YtDlp : MediaTool.Ffmpeg;
    private string ToolName => Tool == MediaTool.Ffmpeg ? "FFmpeg" : "yt-dlp";
    private VersionRow? Selection => VersionList.SelectedItem as VersionRow;

    public sealed record VersionRow(string Tag, bool InUse)
    {
        public string Label => InUse ? $"{Tag}  ·  使用中" : Tag;
    }

    internal ToolVersionsDialog(ToolUpdater updater, Action changed, Func<Task> stopPreview)
    {
        _updater = updater;
        _changed = changed;
        _stopPreview = stopPreview;
        InitializeComponent();
        ToolBox.SelectedIndex = 0;
    }

    private void RefreshVersions(string? selectTag = null)
    {
        VersionRow[] versions = _updater.GetInstalledVersions(Tool)
            .Select(row => new VersionRow(row.Tag, row.InUse)).ToArray();
        VersionRow? active = versions.FirstOrDefault(version => version.InUse);
        ActiveVersion.Text = active is null ? "尚未安装" : $"程序使用版本 {active.Tag}";
        VersionList.ItemsSource = versions;
        VersionList.SelectedItem = versions.FirstOrDefault(version => version.Tag == selectTag) ?? active;
        RefreshAvailableVersions();
    }

    private void RefreshAvailableVersions()
    {
        string? selectedTag = AvailableVersions.SelectedItem as string;
        var installed = VersionList.Items.OfType<VersionRow>().Select(version => version.Tag).ToHashSet(StringComparer.Ordinal);
        string[] choices = _releaseTags.Where(tag => !installed.Contains(tag)).OrderByDescending(Version.Parse).ToArray();
        AvailableVersions.ItemsSource = choices;
        AvailableVersions.SelectedItem = choices.Contains(selectedTag) ? selectedTag : choices.FirstOrDefault();
        UpdateButtons();
    }

    private async void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        await LoadReleasePageAsync(reset: true);
    }

    private async void Tool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ToolBox.SelectedIndex < 0 || AvailableVersions is null)
            return;
        _releaseTags.Clear();
        _nextPage = null;
        _usingWebList = false;
        MessageBar.IsOpen = false;
        RefreshVersions();
        if (_loaded)
            await LoadReleasePageAsync(reset: true);
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void More_Click(object sender, RoutedEventArgs e) => await LoadReleasePageAsync(reset: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadReleasePageAsync(reset: true);

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
        UseButton.IsEnabled = available && Selection is not null;
        DeleteButton.IsEnabled = available && Selection is not null;
        AutoButton.IsEnabled = available && VersionList.Items.Count > 0;
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
        if (Selection is not { } version)
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
        return Task.FromResult("程序将自动使用本地版本号最高的版本。");
    });

    private void Delete_Opening(object sender, object e)
    {
        if (Selection is not { } version)
            return;
        string consequence = !version.InUse ? "" : VersionList.Items.Count == 1
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
            {
                _changed();
                RefreshVersions(selectTag);
            }
            ShowMessage(changesTools ? InfoBarSeverity.Success : InfoBarSeverity.Informational, message);
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested) { }
        catch (OperationCanceledException) { ShowMessage(InfoBarSeverity.Error, "操作超时，请重试。"); }
        catch (Exception ex) { ShowMessage(InfoBarSeverity.Error, ex.Message); }
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
    }

    private void ReportWebFallback(CancellationToken token) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_task?.Token == token)
            ShowMessage(InfoBarSeverity.Informational, "GitHub API 查询失败（HTTP 403），正在使用官方发布页查询。");
    });

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_task is null)
            return;
        args.Cancel = true;
        CancelAndClose();
    }

    internal void CancelAndClose()
    {
        _closeRequested = true;
        if (_task is { } task)
            task.Cancel();
        else
            Hide();
    }
}
