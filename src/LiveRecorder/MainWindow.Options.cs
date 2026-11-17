using System.Text.Json;
using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private string _defaultOutput = "";

    private void RestoreMediaSettings()
    {
        MediaSettings settings = PortableStorage.Configuration.Current.Media;
        _defaultOutput = PortableStorage.DefaultOutputDirectory();
        PreviewVolume.Value = settings.Volume;
        PreviewMute.IsOn = settings.Muted;
        UpdateVolumeGlyph();
        RestoreRecordingReconnectSettings(settings.Reconnect);
        ModeBox.SelectedIndex = settings.Mode;
    }

    private void RestoreRecordingReconnectSettings(RecordingReconnectSettings reconnect)
    {
        Initialize(RecordingStallBox, reconnect.StallSeconds, RecordingReconnectSettings.MaxSeconds);
        Initialize(RecordingRetryBox, reconnect.RetrySeconds, RecordingReconnectSettings.MaxSeconds);
        Initialize(RecordingWaitBox, reconnect.MaxWaitMinutes, RecordingReconnectSettings.MaxMinutes);

        void Initialize(NumberBox box, int value, int maximum)
        {
            box.Minimum = 1;
            box.Maximum = maximum;
            box.Value = value;
            box.ValueChanged += RecordingReconnectSetting_Changed;
        }
    }

    private void RecordingReconnectSetting_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!IsIntegerInRange(e.NewValue, 1, (int)sender.Maximum))
        {
            sender.Value = e.OldValue;
            return;
        }

        var reconnect = new RecordingReconnectSettings((int)RecordingStallBox.Value, (int)RecordingRetryBox.Value,
            (int)RecordingWaitBox.Value);
        try
        {
            PortableStorage.Configuration.Update(settings => settings with { Media = settings.Media with { Reconnect = reconnect } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus("无法保存重连设置", ex.Message, LogLevel.Error, ex);
        }
    }

    private void SaveMediaSettings()
    {
        MediaSettings previous = PortableStorage.Configuration.Current.Media;
        int mode = (int)_mode;
        int volume = (int)PreviewVolume.Value;
        bool muted = PreviewMute.IsOn;
        if (previous.Mode == mode && previous.Volume == volume && previous.Muted == muted)
            return;
        try
        {
            PortableStorage.Configuration.Update(settings =>
                settings with { Media = settings.Media with { Mode = mode, Volume = volume, Muted = muted } });
            PortableStorage.Log.Write(LogLevel.Debug, "Configuration", "已保存媒体设置。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PortableStorage.Log.Write(LogLevel.Error, "Configuration", "无法保存媒体设置。", ex);
        }
    }

    private async void OutputOptions_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        var path = new TextBox { Header = "输出文件夹", Text = PortableStorage.Configuration.Current.Media.OutputDirectory ?? _defaultOutput };
        var browse = new Button { Content = "浏览…" };
        browse.Click += async (_, _) => await HandlePickerAsync(async () =>
        {
            if (await PickFolderAsync() is { } folder)
                path.Text = folder;
        });
        var useDefault = new Button { Content = "使用默认目录" };
        useDefault.Click += (_, _) => path.Text = _defaultOutput;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(browse);
        buttons.Children.Add(useDefault);
        var panel = FormPanel(path, buttons);
        if (await ShowOptionsAsync("保存到", panel, () =>
        {
            string directory = NormalizeOutputDirectory(path.Text);
            string? custom = directory.Equals(_defaultOutput, StringComparison.OrdinalIgnoreCase) ? null : directory;
            PortableStorage.Configuration.Update(settings => settings with { Media = settings.Media with { OutputDirectory = custom } });
        }))
        {
            _lastResultFolder = null;
            SetStatus("输出目录已保存", OutputDirectory());
        }
    });

    private async void EncodingOptions_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        MediaSettings media = PortableStorage.Configuration.Current.Media;
        EncodingSettings previous = media.Encoding;
        var encoder = SettingChoice("编码器", (int)media.Encoder, "软件", "Intel · Quick Sync", "NVIDIA · NVENC", "AMD · AMF");
        var keepVideoFormat = SettingCheck("保持原格式", previous.KeepVideoFormat);
        var codec = SettingChoice("视频编码格式", (int)previous.VideoCodec, "H.264 (AVC)", "H.265 (HEVC)");
        var deinterlace = SettingChoice("去隔行", (int)media.Deinterlace, "自动（重编码时依据视频标记）", "强制去隔行", "关闭");
        var part = SettingNumber("录制分片大小（MB）", media.PartSizeMb, 1, 1024);
        var reencode = SettingCheck("合并时使用所选编码器重新编码", media.Reencode);
        var resolution = SettingChoice("转码分辨率", (int)previous.Resolution,
            "640 × 360", "854 × 480", "1280 × 720", "1920 × 1080", "2560 × 1440", "3840 × 2160");
        var keepResolution = SettingCheck("保持原分辨率", previous.KeepResolution);

        var rate = new RadioButtons { Header = "视频编码方式", MaxColumns = 2, ItemsSource = new[] { "码率", "质量" },
            SelectedIndex = (int)previous.RateMode };
        var rateMode = SettingChoice("码率模式", (int)previous.BitrateMode, "可变码率（VBR）", "恒定码率（CBR）");
        var keepVideoBitrate = SettingCheck("保持原码率", previous.KeepVideoBitrate);
        int[] videoRatesKbps = [256, 500, 1000, 1500, 2000, 2500, 4000, 6000, 8000, 12000, 16000, 25000, 40000, 60000, 100000];
        double previousRateKbps = previous.VideoBitrate * (previous.VideoBitrateUnit == BitrateUnit.Mbps ? 1000 : 1);
        int videoPresetIndex = Array.FindIndex(videoRatesKbps, value => value == previousRateKbps);
        var videoPreset = SettingChoice("视频码率", videoPresetIndex < 0 ? videoRatesKbps.Length : videoPresetIndex,
            [.. videoRatesKbps.Select(value => value < 1000 ? $"{value} kbps" : $"{value / 1000d:0.###} Mbps"), "自定义…"]);
        var videoBitrate = SettingNumber("自定义视频码率", previous.VideoBitrate, 0.001, 1_000_000);
        var unit = SettingChoice("单位", (int)previous.VideoBitrateUnit, "kbps", "Mbps");
        var customVideoRate = new Grid { ColumnSpacing = 10 };
        customVideoRate.ColumnDefinitions.Add(new ColumnDefinition());
        customVideoRate.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(unit, 1);
        customVideoRate.Children.Add(videoBitrate);
        customVideoRate.Children.Add(unit);
        var rates = FormPanel(keepVideoBitrate, rateMode, videoPreset, customVideoRate);
        var quality = SettingChoice("编码质量", (int)previous.Quality, "较小文件", "标准", "较高质量");

        var keepAudioFormat = SettingCheck("保持原格式", previous.KeepAudioFormat);
        var audioCodec = SettingChoice("音频格式", (int)previous.AudioFormat, "AAC", "MP3", "AC-3", "Opus");
        var keepSamples = SettingCheck("保持原采样率", previous.KeepAudioSampleRate);
        int[] sampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 176400, 192000];
        sampleRates = sampleRates.Append(previous.AudioSampleRate).Distinct().Order().ToArray();
        var samples = SettingChoice("音频采样率", Array.IndexOf(sampleRates, previous.AudioSampleRate),
            sampleRates.Select(value => $"{value} Hz").ToArray());
        var keepAudioBitrate = SettingCheck("保持原码率", previous.KeepAudioBitrate);
        int[] audioRatesKbps = [8, 16, 24, 32, 48, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640];
        int audioPresetIndex = Array.IndexOf(audioRatesKbps, previous.AudioBitrateKbps);
        var audioPreset = SettingChoice("音频码率", audioPresetIndex < 0 ? audioRatesKbps.Length : audioPresetIndex,
            [.. audioRatesKbps.Select(value => $"{value} kbps"), "自定义…"]);
        var audioBitrate = SettingNumber("自定义音频码率（kbps）", previous.AudioBitrateKbps, 8, 1536);
        var panel = FormPanel(
            Section("视频"), keepVideoFormat, codec, encoder, deinterlace, keepResolution, resolution, rate, rates, quality,
            SectionDivider(), Section("音频"), keepAudioFormat, audioCodec, keepSamples, samples,
            keepAudioBitrate, audioPreset, audioBitrate,
            SectionDivider(), Section("录制和合并"), part, reencode);

        rate.SelectionChanged += (_, _) => SyncControls();
        videoPreset.SelectionChanged += (_, _) => SyncControls();
        audioPreset.SelectionChanged += (_, _) => SyncControls();
        foreach (CheckBox box in new[] { keepVideoFormat, keepResolution, keepVideoBitrate, keepAudioFormat, keepSamples, keepAudioBitrate })
        {
            box.Checked += (_, _) => SyncControls();
            box.Unchecked += (_, _) => SyncControls();
        }
        SyncControls();

        if (await ShowOptionsAsync("编码设置", panel, Save, scrollWidth: 516))
            SetStatus("编码设置已保存", "转码、重新编码合并，以及原流无法直接封装的录制将使用这些设置。");

        void SyncControls()
        {
            codec.IsEnabled = keepVideoFormat.IsChecked != true;
            resolution.IsEnabled = keepResolution.IsChecked != true;
            rates.Visibility = rate.SelectedIndex == (int)VideoRateMode.Bitrate ? Visibility.Visible : Visibility.Collapsed;
            quality.Visibility = rate.SelectedIndex == (int)VideoRateMode.Quality ? Visibility.Visible : Visibility.Collapsed;
            rateMode.IsEnabled = videoPreset.IsEnabled = keepVideoBitrate.IsChecked != true;
            customVideoRate.Visibility = videoPreset.SelectedIndex == videoRatesKbps.Length && videoPreset.IsEnabled
                ? Visibility.Visible : Visibility.Collapsed;
            audioCodec.IsEnabled = keepAudioFormat.IsChecked != true;
            samples.IsEnabled = keepSamples.IsChecked != true;
            audioPreset.IsEnabled = keepAudioBitrate.IsChecked != true;
            audioBitrate.Visibility = audioPreset.SelectedIndex == audioRatesKbps.Length && audioPreset.IsEnabled
                ? Visibility.Visible : Visibility.Collapsed;
        }

        void Save()
        {
            var bitrateUnit = (BitrateUnit)unit.SelectedIndex;
            double selectedVideoRate = videoBitrate.Value;
            if (videoPreset.SelectedIndex < videoRatesKbps.Length)
            {
                int kbps = videoRatesKbps[videoPreset.SelectedIndex];
                bitrateUnit = kbps < 1000 ? BitrateUnit.Kbps : BitrateUnit.Mbps;
                selectedVideoRate = bitrateUnit == BitrateUnit.Kbps ? kbps : kbps / 1000d;
            }
            double selectedAudioRate = audioPreset.SelectedIndex < audioRatesKbps.Length
                ? audioRatesKbps[audioPreset.SelectedIndex] : audioBitrate.Value;
            bool validVideoRate = EncodingSettings.ValidVideoBitrate(selectedVideoRate, bitrateUnit);
            if (!IsIntegerInRange(part.Value, 1, 1024))
                throw new ArgumentException("录制分片大小应为 1 到 1024 MB 的整数。");
            if (rate.SelectedIndex == (int)VideoRateMode.Bitrate && keepVideoBitrate.IsChecked != true && !validVideoRate)
                throw new ArgumentException("视频码率应为 1 kbps 到 1000 Mbps。");
            if (keepAudioBitrate.IsChecked != true && !IsIntegerInRange(selectedAudioRate, 8, 1536))
                throw new ArgumentException("音频码率应为 8 到 1536 kbps 的整数。");

            var next = new EncodingSettings
            {
                VideoCodec = (VideoCodec)codec.SelectedIndex, KeepVideoFormat = keepVideoFormat.IsChecked == true,
                KeepResolution = keepResolution.IsChecked == true,
                Resolution = (VideoResolution)resolution.SelectedIndex, RateMode = (VideoRateMode)rate.SelectedIndex,
                BitrateMode = (BitrateMode)rateMode.SelectedIndex, KeepVideoBitrate = keepVideoBitrate.IsChecked == true,
                VideoBitrate = validVideoRate ? selectedVideoRate : previous.VideoBitrate,
                VideoBitrateUnit = validVideoRate ? bitrateUnit : previous.VideoBitrateUnit,
                Quality = (EncodingQuality)quality.SelectedIndex, KeepAudioFormat = keepAudioFormat.IsChecked == true,
                AudioFormat = (AudioFormat)audioCodec.SelectedIndex, KeepAudioSampleRate = keepSamples.IsChecked == true,
                AudioSampleRate = sampleRates[samples.SelectedIndex],
                KeepAudioBitrate = keepAudioBitrate.IsChecked == true,
                AudioBitrateKbps = IsIntegerInRange(selectedAudioRate, 8, 1536) ? (int)selectedAudioRate : previous.AudioBitrateKbps
            };
            PortableStorage.Configuration.Update(settings => settings with
            {
                Media = settings.Media with
                {
                    Encoder = (VideoEncoder)encoder.SelectedIndex, Deinterlace = (DeinterlaceMode)deinterlace.SelectedIndex,
                    PartSizeMb = (int)part.Value, Reencode = reencode.IsChecked == true, Encoding = next
                }
            });
        }
    });

    private async Task<bool> ShowOptionsAsync(string title, FrameworkElement content, Action save, double? scrollWidth = null)
    {
        XamlRoot root = ((FrameworkElement)Content).XamlRoot;
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = FormPanel(error, content)
        };
        ((StackPanel)body.Content).Padding = new Thickness(0, 0, 12, 12);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = body,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (scrollWidth is double targetWidth)
            dialog.Resources["ContentDialogMaxWidth"] = targetWidth + 48;
        UpdateBounds();
        root.Changed += RootChanged;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                args.Cancel = true;
                error.Message = ex.Message;
                error.IsOpen = true;
                body.ChangeView(null, 0, null);
            }
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { root.Changed -= RootChanged; }

        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

        void UpdateBounds()
        {
            body.MaxHeight = Math.Clamp(root.Size.Height - 196, 80, 550);
            if (scrollWidth is double width)
                body.Width = Math.Clamp(root.Size.Width - 96, 200, width);
        }
    }

    private static StackPanel FormPanel(params UIElement[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (UIElement control in controls)
            panel.Children.Add(control);
        return panel;
    }

    private static TextBlock Section(string title) => new() { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static Border SectionDivider() => new()
    {
        Height = 2, Margin = new Thickness(0, 12, 0, 12),
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"]
    };

    private static ComboBox SettingChoice(string title, int selected, params string[] values) => new()
    {
        Header = title, ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static CheckBox SettingCheck(string label, bool isChecked) => new() { Content = label, IsChecked = isChecked };

    private static NumberBox SettingNumber(string title, double value, double minimum, double maximum) => new()
    {
        Header = title, Minimum = minimum, Maximum = maximum, Value = value,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };

    private async void LogOptions_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        LoggingSettings settings = PortableStorage.Configuration.Current.Logging;
        LogLevel[] choices = Enum.GetValues<LogLevel>();
        var levels = new ComboBox
        {
            Header = "日志级别", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Debug", "Info", "Warning", "Error", "关闭日志" },
            SelectedIndex = Array.IndexOf(choices, settings.Level)
        };
        var size = CreateLogNumberBox("单个日志文件大小（MB）", settings.FileSizeMb);
        var count = CreateLogNumberBox("最多保留文件数", settings.FileCount);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var options = new StackPanel { Spacing = 12 };
        options.Children.Add(levels);
        options.Children.Add(size);
        options.Children.Add(count);
        options.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "日志设置",
            Content = options,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (levels.SelectedIndex < 0 || !IsIntegerInRange(size.Value, 1, 100) || !IsIntegerInRange(count.Value, 1, 100))
            {
                args.Cancel = true;
                error.Message = "请选定日志级别，大小和保留数量应为 1 到 100 的整数。";
                error.IsOpen = true;
                return;
            }
            var next = new LoggingSettings(choices[levels.SelectedIndex], (int)size.Value, (int)count.Value);
            try
            {
                PortableStorage.Configuration.Update(current => current with { Logging = next });
                PortableStorage.Log.Configure(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                args.Cancel = true;
                error.Message = $"无法保存日志设置。{ex.Message}";
                error.IsOpen = true;
                PortableStorage.Log.Write(LogLevel.Error, "Configuration", "无法保存日志设置。", ex);
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            SetStatus("日志设置已保存", $"{levels.SelectedItem} · {size.Value:0} MB · 保留 {count.Value:0} 个文件");
    });

    private static NumberBox CreateLogNumberBox(string label, int value) => new()
    {
        Header = label, Minimum = 1, Maximum = 100, Value = value,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };

    private static bool IsIntegerInRange(double value, int minimum, int maximum) =>
        double.IsInteger(value) && value >= minimum && value <= maximum;
}
