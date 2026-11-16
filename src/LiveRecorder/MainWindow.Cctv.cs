using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private readonly ICctvLinkResolver _cctvResolver;

    private void CctvSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CctvEpisodeList is null)
            return;
        CctvEpisodeList.ItemsSource = null;
        CctvEpisodePanel.Visibility = Visibility.Collapsed;
    }

    private async void AnalyzeCctv_Click(object sender, RoutedEventArgs e) => await RunAsync("解析 CCTV 链接中", async token =>
    {
        if (!Uri.TryCreate(CctvSourceBox.Text.Trim(), UriKind.Absolute, out Uri? page))
            throw new ArgumentException("请输入完整的 CCTV 网页链接。");

        CctvEpisodeList.ItemsSource = null;
        CctvEpisodePanel.Visibility = Visibility.Collapsed;
        IReadOnlyList<CctvEpisode> episodes = await _cctvResolver.GetEpisodesAsync(page, CctvIncludeAlbumBox.IsChecked == true, token);
        CctvEpisodeList.ItemsSource = episodes;
        CctvEpisodeList.SelectedItem = episodes.FirstOrDefault(episode => episode.Page.AbsolutePath == page.AbsolutePath)
            ?? episodes[0];
        CctvEpisodePanel.Visibility = Visibility.Visible;
        SetStatus("解析完成", $"找到 {episodes.Count} 集。勾选剧集后，点击右侧「预览链接」或「下载视频」。");
    });

    private void SelectAllCctv_Click(object sender, RoutedEventArgs e) => CctvEpisodeList.SelectAll();

    private void ClearCctv_Click(object sender, RoutedEventArgs e) => CctvEpisodeList.SelectedItems.Clear();

    private CctvQuality SelectedCctvQuality() => (CctvQuality?)CctvQualityBox.SelectedItem ?? CctvQuality.All[0];

    private static string CctvSourceDetail(CctvMediaLink link)
    {
        if (link.Quality is not { } quality)
            return "HLS 直链";
        return $"实际使用 {quality.Label} · 央视清单 {string.Join("、", link.ListedQualities.Select(level => level.Label))}";
    }

    private CctvEpisode[] SelectedCctvEpisodes()
    {
        var selected = CctvEpisodeList.SelectedItems.OfType<CctvEpisode>().ToHashSet();
        if (selected.Count == 0)
            throw new ArgumentException("请先解析 CCTV 链接并勾选剧集。");
        return CctvEpisodeList.Items.OfType<CctvEpisode>().Where(selected.Contains).ToArray();
    }

    private async Task DownloadCctvAsync(CancellationToken token)
    {
        CctvEpisode[] episodes = SelectedCctvEpisodes();
        CctvQuality quality = SelectedCctvQuality();
        string directory = OutputDirectory();
        int completed = 0;
        int downgraded = 0;
        var failures = new List<string>();
        for (int index = 0; index < episodes.Length; index++)
        {
            CctvEpisode episode = episodes[index];
            string stage = $"下载中 · {index + 1}/{episodes.Length}";
            StartStage(stage);
            SetStatus(stage, episode.Title);
            try
            {
                token.ThrowIfCancellationRequested();
                CctvMediaLink source = await _cctvResolver.ResolveAsync(episode, quality, token);
                SetStatus($"{stage} · {source.Quality?.Label ?? "HLS"}", episode.Title);
                string output = await _media.DownloadAsync(source.Url, directory, CreateProgress(), token, episode.Title);
                ShowOutput("下载完成", output);
                if (source.Quality is { } actual && actual.Code < quality.Code)
                    downgraded++;
                if (episodes.Length == 1)
                    SetStatus($"下载完成 · {CctvSourceDetail(source)}", output);
                completed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SetStatus("已停止", $"已保存 {completed}/{episodes.Length} 集。");
                return;
            }
            catch (Exception ex) when (episodes.Length > 1 && !token.IsCancellationRequested)
            {
                string detail = ex is OperationCanceledException ? "连接超时。" : ex.Message;
                failures.Add($"{episode.Title} · {detail}");
            }
            finally { ++_progressVersion; }
        }

        string fallback = downgraded > 0 ? $" · {downgraded} 集使用较低档位" : "";
        if (failures.Count > 0)
            SetStatus($"已保存 {completed} 集，失败 {failures.Count} 集{fallback}", string.Join("\n", failures.Take(2)));
        else if (episodes.Length > 1)
            SetStatus($"已下载 {completed} 集{fallback}", directory);
    }
}
