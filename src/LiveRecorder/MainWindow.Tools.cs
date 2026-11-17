using LiveRecorder.Core;
using Microsoft.UI.Xaml;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private readonly ToolUpdater _toolUpdater = new(PortableStorage.ToolsDirectory, PortableStorage.CacheDirectory);
    private string? _ffmpegPath;
    private ToolVersionsDialog? _toolVersionsDialog;

    private void ReloadMediaTools() => (_media, _webAnalyzer, _ffmpegPath) = ResolveMediaTools();

    private (MediaService Media, WebVideoAnalyzer Web, string? FfmpegPath) ResolveMediaTools()
    {
        string? ffmpeg = _toolUpdater.GetExecutable(MediaTool.Ffmpeg);
        string? ytDlp = _toolUpdater.GetExecutable(MediaTool.YtDlp);
        return (new MediaService(ffmpeg, PortableStorage.CacheDirectory),
            new WebVideoAnalyzer(ytDlp, _toolUpdater.DenoPath, PortableStorage.CacheDirectory), ffmpeg);
    }

    private async void UpgradeFfmpeg_Click(object sender, RoutedEventArgs e) => await UpgradeToolAsync(MediaTool.Ffmpeg, "FFmpeg");

    private async void UpgradeYtDlp_Click(object sender, RoutedEventArgs e) => await UpgradeToolAsync(MediaTool.YtDlp, "yt-dlp");

    private async void ManageToolVersions_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        if (_operation is not null || _toolVersionsDialog is not null || _closeWhenIdle)
            return;
        var dialog = new ToolVersionsDialog(_toolUpdater, ReloadMediaTools, StopPreviewAsync)
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

    private Task UpgradeToolAsync(MediaTool tool, string name) => RunAsync($"正在检查 {name}", async token =>
    {
        SetStatus($"正在检查 {name}", "正在通过 GitHub API 核对当前版本和上游稳定版。");
        bool finished = false;
        ToolUpdateResult result;
        try
        {
            result = await _toolUpdater.UpdateAsync(tool, CreateProgress(), token, () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!finished && !token.IsCancellationRequested)
                    SetStatus($"正在检查 {name}", "GitHub API 查询失败（HTTP 403），正在使用官方发布页查询。");
            }));
        }
        finally
        {
            finished = true;
        }
        string source = result.UsedWebFallback ? "GitHub API 查询失败（HTTP 403），已使用官方发布页查询。\n" : "";
        string active = $"\n程序使用版本 {_toolUpdater.GetActiveTag(tool)}。";
        if (result.Updated)
        {
            ReloadMediaTools();
            if (result.Before is null)
                SetStatus($"{name} 已安装", $"{source}版本 {result.After}，SHA-256 与运行版本校验通过。{active}");
            else
                SetStatus($"{name} 已升级", $"{source}{result.Before} → {result.After}，SHA-256 与运行版本校验通过。{active}");
        }
        else
        {
            SetStatus($"{name} 无需升级", $"{source}本地最新版本为 {result.After}，已核对上游稳定版。{active}");
        }
    });
}
