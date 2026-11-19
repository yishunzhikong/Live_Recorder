using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private const string ProjectPage = "https://github.com/yishunzhikong/Live_Recorder";

    /// <summary>显示版本、更新操作和项目链接。</summary>
    private async void About_Click(object sender, RoutedEventArgs e) => await MediaPane.HandlePickerAsync(ShowAboutAsync);

    private async Task ShowAboutAsync()
    {
        if (MediaPane.IsBusy || _activeDialog is not null || _closeWhenIdle)
            return;

        Version assemblyVersion = typeof(App).Assembly.GetName().Version!;
        var current = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
        using var cancellation = new CancellationTokenSource();
        ApplicationUpdate? update = null;
        string? staging = null;
        Task operation = Task.CompletedTask;

        var check = new Button { Content = "检查更新" };
        var install = new Button { Content = "下载并更新", Visibility = Visibility.Collapsed };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(check);
        actions.Children.Add(install);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Visibility = Visibility.Collapsed };
        var links = new StackPanel();
        foreach (var (title, path) in new[] { ("GitHub 项目", ""), ("反馈问题", "/issues/new"),
            ("版本发布", "/releases"), ("更新日志", "/releases/latest") })
            links.Children.Add(new HyperlinkButton { Content = title, NavigateUri = new Uri(ProjectPage + path) });

        var dialog = new ContentDialog
        {
            Title = "关于 Live Recorder",
            Content = FormPanel(new TextBlock { Text = $"版本 v{current}" }, actions, status, progress, links),
            CloseButtonText = "关闭"
        };
        check.Click += (_, _) => operation = RunAsync(CheckAsync);
        install.Click += (_, _) => operation = RunAsync(DownloadAsync);
        dialog.Closing += (_, _) =>
        {
            if (staging is null)
                cancellation.Cancel();
        };

        try
        {
            await ShowDialogAsync(dialog);
            await operation;
            if (staging is null || cancellation.IsCancellationRequested || _closeWhenIdle)
                return;

            await MediaPane.StopPreviewAsync();
            if (_closeWhenIdle)
                return;
            PortableApplicationUpdate.Start(staging);
            staging = null;
            Close();
        }
        finally
        {
            if (staging is not null)
                PortableApplicationUpdate.Discard(staging);
        }

        async Task RunAsync(Func<Task> action)
        {
            check.IsEnabled = install.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                status.Text = ex is OperationCanceledException ? "连接超时，请重试。" : ex.Message;
                WriteWindowLog(LogLevel.Warning, "Update", "应用更新未完成。", ex);
            }
            finally
            {
                if (staging is null && !cancellation.IsCancellationRequested)
                {
                    check.IsEnabled = install.IsEnabled = true;
                    progress.Visibility = Visibility.Collapsed;
                }
            }
        }

        async Task CheckAsync()
        {
            update = null;
            install.Visibility = Visibility.Collapsed;
            status.Text = "正在通过 GitHub API 检查更新…";
            bool finished = false;
            try
            {
                var result = await _toolUpdater.CheckApplicationUpdateAsync(current, cancellation.Token,
                    () => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!finished && !cancellation.IsCancellationRequested)
                            status.Text = "GitHub API 查询失败（HTTP 403），正在使用官方发布页查询。";
                    }));
                update = result.Update;
                string fallback = result.UsedWebFallback ? "GitHub API 返回 HTTP 403，已使用官方发布页。\n" : "";
                status.Text = fallback + (update is null ? "当前已是最新版本。" :
                    $"发现 {update.Tag}。更新将保留 logs、config 和 tools，并替换程序目录中的其余文件和目录。更新后会重新启动。");
                install.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { finished = true; }
        }

        async Task DownloadAsync()
        {
            if (update is not { } available)
                return;
            status.Text = $"正在下载并验证 {available.Tag}…";
            progress.Value = 0;
            progress.IsIndeterminate = true;
            progress.Visibility = Visibility.Visible;
            var report = new Progress<MediaProgress>(value =>
            {
                if (!cancellation.IsCancellationRequested && value.TotalBytes is > 0 && value.Bytes is long bytes)
                {
                    progress.IsIndeterminate = false;
                    progress.Value = Math.Clamp(bytes * 100d / value.TotalBytes.Value, 0, 100);
                }
            });
            staging = await PortableApplicationUpdate.PrepareAsync(_toolUpdater, available, report, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                dialog.Hide();
        }
    }
}
