# Live Recorder

Live Recorder 是使用 C#、WinUI 3 和 Windows App SDK 构建的 Windows 视频下载和录制工具。`NOCCTV` 版本提供「直播录制」和「yt-dlp」两种模式，支持 Windows 10 2004（19041）及以上版本和 Windows 11 的 x64 系统。

## 运行发布版

完整解压 `LiveRecorder-NOCCTV-win-x64.zip`，打开解压后的 `LiveRecorder-NOCCTV-win-x64` 文件夹并双击 `LiveRecorder.exe`。程序会自动使用随包附带的 FFmpeg、yt-dlp 和 Deno。

## 直播录制

左侧「模式」默认选择「直播录制」。在「媒体链接」中粘贴直链，再通过右侧工具栏操作。

- 点击「预览链接」播放当前链接。也可以打开本地视频，或播放最近一次下载、合并或转码的结果。播放器提供播放、暂停、音量和可用时的进度拖动。
- 点击「下载视频」保存 HTTP(S) 视频原文件。HLS（M3U/M3U8）会按播放列表读取分片并封装成 MKV。
- 点击「录制直播」录制 HLS 直播、RTSP(S)、RTMP(S)、RTP、UDP 或 SRT。RTP 动态载荷需要 SDP 描述时，通过「更多操作」中的「从 SDP 录制」选择本地文件。
- 录制结束或手动停止后，可以选择原样合并、用所选编码器重新编码后合并，或保留分片。稍后可在「更多操作」中选择「合并录制分片」。转码与合并均输出 MP4，并保留输入文件。

右侧的预览区占纵向的四分之三。播放器的单行控制栏从左到右显示操作按钮、进度条和当前时间及总时长。下方工具栏提供预览、本地播放、下载和录制；SDP 录制、合并、转码和打开文件夹可从「更多操作」菜单进入。

本地文件与 HTTP(S) 链接的播放格式取决于 Windows 的媒体解码支持。RTSP、RTMP、RTP、UDP 和 SRT 由内置 FFmpeg 转换为可预览的流，通常有数秒缓冲延迟。录制占用同一 RTP 或 UDP 端口时，应用会先释放预览连接。

## yt-dlp

切换到「yt-dlp」，粘贴网页地址并点击「分析链接」。在「媒体格式」中选定一项后，可用右侧的「预览链接」或「下载视频」，也可在左侧点击「复制直链」。选择「音频」格式时，yt-dlp 会下载所选音轨，再准备为 M4A 供预览或保存。下载文件按完成时间命名，例如 `audio_20261117_020000.m4a`，同名时追加 ` (2)`、` (3)`。音频预览开始前需要等待文件处理完成。直链可能过期，也可能需要原网站的请求头。

## 保存和编码

默认保存到 Windows「视频」中的 `Live Recorder` 文件夹。每次录制使用一个独立的文件夹，默认分片上限为 1 MB（1,000,000 字节）。分片在 MPEG-TS 的 188 字节包边界切开，最后一片可以更小。分片面向顺序合并，单片可能需要前一片的关键帧才能连贯播放。

录制文件夹按本机开始时间命名，如 `record_20261116_210530`。合并后的 MP4 沿用分片文件夹名。同名时依次使用 ` (2)`、` (3)` 等后缀，如 `record_20261116_210530 (2).mp4`。

录制和转码默认会在缩放前处理标记为隔行的画面。如果字幕或移动边缘仍有梳齿，可在「去隔行」中选择「强制去隔行」。已有视频可以用同一设置转码，已有分片可勾选重新编码后再合并。原样合并保留原有画面。

录制和转码可选软件 x264、Intel Quick Sync、NVIDIA NVENC、AMD AMF。硬件编码要求对应的显卡和驱动。原样合并直接复制已录制的音视频流。每个任务保留首路视频和首路音频，纯音频流也可以处理。下载适用于直接媒体链接和 HLS，普通网页播放器地址需先取得媒体链接。

## 从源码构建

安装 .NET 10 SDK 后，从 `NOCCTV` 分支的仓库根目录运行以下命令。[下载 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
dotnet build src/LiveRecorder/LiveRecorder.csproj -c Release
dotnet publish src/LiveRecorder/LiveRecorder.csproj -c Release -o artifacts/LiveRecorder-NOCCTV-win-x64
Copy-Item -LiteralPath README.md -Destination artifacts/LiveRecorder-NOCCTV-win-x64/
tar.exe -a -cf artifacts/LiveRecorder-NOCCTV-win-x64.zip --exclude='*.pdb' -C artifacts LiveRecorder-NOCCTV-win-x64
```

首次构建时会自动下载并校验固定版本的 FFmpeg、yt-dlp 和 Deno。发布目录包含这些依赖以及 .NET 和 Windows App SDK 运行时。目标机器还需 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。

随包分发的 FFmpeg 是 [Gyan FFmpeg 9.0.2 Essentials](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2) 的 Windows x64 构建，按 GPL v3 授权。发布包的 `ffmpeg` 子目录附带许可证和原始构建说明。对应源码见 [FFmpeg 9.0.2 源码版本](https://github.com/FFmpeg/FFmpeg/tree/946fcce07b)，协议细节见 [FFmpeg 协议文档](https://ffmpeg.org/ffmpeg-protocols.html)。

网页分析使用 [yt-dlp 2026.10.26](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26) 的官方 Windows 可执行文件，以及供其运行网页解析脚本的 [Deno 2.9.8](https://github.com/denoland/deno/releases/tag/v2.9.8)。发布包的 `yt-dlp` 子目录附带源码项目的许可证。yt-dlp 官方打包的可执行文件按 GPL v3 或更高版本授权，GPL v3 全文见 `ffmpeg/LICENSE`。
