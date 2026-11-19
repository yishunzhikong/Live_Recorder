using LiveRecorder.Core;
using Microsoft.UI.Xaml;

namespace LiveRecorder;

// 连接工具菜单、版本管理对话框和媒体服务。
public sealed partial class MainWindow
{
    private readonly ToolUpdater _toolUpdater = new(PortableStorage.ToolsDirectory, PortableStorage.CacheDirectory,
        configuration: PortableStorage.Configuration);
    private string? _ffmpegPath;
    private ToolVersionsDialog? _toolVersionsDialog;

    /// <summary>使用同一份安装快照刷新指定媒体服务。</summary>
    /// <param name="tool">需要重新绑定的工具。</param>
    /// <param name="installed">同时供版本显示和后续媒体任务使用的安装快照。</param>
    private void ReloadMediaTool(MediaTool tool, ToolInstallation installed)
    {
        if (tool == MediaTool.Ffmpeg)
        {
            _ffmpegPath = installed.ActiveExecutable;
        }
        else
        {
            _webAnalyzer = new WebVideoAnalyzer(installed.ActiveExecutable, _toolUpdater.DenoPath, PortableStorage.CacheDirectory);
        }
        MediaPane.SetTools(_ffmpegPath, _webAnalyzer);
    }

    private (WebVideoAnalyzer Web, string? FfmpegPath) ResolveMediaTools()
    {
        string? ffmpeg = _toolUpdater.GetExecutable(MediaTool.Ffmpeg);
        string? ytDlp = _toolUpdater.GetExecutable(MediaTool.YtDlp);
        return (new WebVideoAnalyzer(ytDlp, _toolUpdater.DenoPath, PortableStorage.CacheDirectory), ffmpeg);
    }

    private async void UpgradeFfmpeg_Click(object sender, RoutedEventArgs e) => await UpgradeToolAsync(MediaTool.Ffmpeg, "FFmpeg");

    private async void UpgradeYtDlp_Click(object sender, RoutedEventArgs e) => await UpgradeToolAsync(MediaTool.YtDlp, "yt-dlp");

    private async void ManageToolVersions_Click(object sender, RoutedEventArgs e) => await MediaPane.HandlePickerAsync(async () =>
    {
        if (MediaPane.IsBusy || _toolVersionsDialog is not null || _closeWhenIdle)
            return;
        var dialog = new ToolVersionsDialog(_toolUpdater, ReloadMediaTool, MediaPane.StopPreviewAsync, _logWindowId)
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        _toolVersionsDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally
        {
            _toolVersionsDialog = null;
            CloseWhenReady();
        }
    });

    private Task UpgradeToolAsync(MediaTool tool, string name) => MediaPane.RunAsync($"正在检查 {name}", async token =>
    {
        MediaPane.SetStatus($"正在检查 {name}", "正在通过 GitHub API 核对当前版本和上游稳定版。");
        bool finished = false;
        ToolUpdateResult result;
        try
        {
            result = await _toolUpdater.UpdateAsync(tool, MediaPane.CreateProgress(), token, () => DispatcherQueue.TryEnqueue(() =>
            {
                // 排队到界面线程的回退提示可能晚于任务结果，此时保留最终状态。
                if (!finished && !token.IsCancellationRequested)
                    MediaPane.SetStatus($"正在检查 {name}", "GitHub API 查询失败（HTTP 403），正在使用官方发布页查询。", LogLevel.Warning);
            }));
        }
        finally
        {
            finished = true;
        }
        string source = result.UsedWebFallback ? "GitHub API 查询失败（HTTP 403），已使用官方发布页查询。\n" : "";
        ToolInstallation installed = _toolUpdater.GetInstallation(tool);
        ReloadMediaTool(tool, installed);
        string active = installed.ActiveTag is { } tag ? $"\n程序使用版本 {tag}。" : "\n没有可用版本。";
        if (result.Updated)
        {
            if (result.Before is null)
                MediaPane.SetStatus($"{name} 已安装", $"{source}版本 {result.After}，SHA-256 与运行版本校验通过。{active}");
            else
                MediaPane.SetStatus($"{name} 已升级", $"{source}{result.Before} → {result.After}，SHA-256 与运行版本校验通过。{active}");
        }
        else
        {
            MediaPane.SetStatus($"{name} 无需升级", $"{source}本地最新版本为 {result.After}，已核对上游稳定版。{active}");
        }
    });
}
