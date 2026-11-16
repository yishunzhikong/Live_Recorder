# Live Recorder

Live Recorder 是使用 C#、WinUI 3 和 Windows App SDK 构建的 Windows 视频下载和录制工具。支持 Windows 10 2004（19041）及以上版本、Windows 11，提供 x64 构建。

## 使用

1. 完整解压发布包，再双击 `LiveRecorder.exe`。应用会自动使用随包附带的 FFmpeg。
2. 在左侧设置中粘贴链接。点击「预览链接」在右上方播放，也可打开本地视频或播放最近一次下载、合并或转码的结果。播放器提供播放、暂停、音量和可用时的进度拖动。
3. 对于 HTTP(S) 视频文件，点「下载视频」即可保存原始文件。对于 HLS（M3U/M3U8），应用按播放列表读取分片并自动封装成 MKV。
4. 对于 HLS 直播、RTSP(S)、RTMP(S)、RTP、UDP 或 SRT，点「录制直播」。RTP 动态载荷需要 SDP 描述时，点「从 SDP 录制」选择本地文件。
5. 录制结束或手动停止后，可在弹窗里选择原样合并、用所选编码器重新编码后合并，或保留分片。稍后可通过「合并录制分片」选中该次录制的文件夹。转码与合并均输出 MP4，并保留输入文件。

右侧的预览区占纵向的四分之三。播放器的单行控制栏从左到右显示操作按钮、进度条和当前时间及总时长。下方工具栏提供预览、本地播放、下载和录制；SDP 录制、合并、转码和打开文件夹可从「更多操作」菜单进入。

左侧「模式」默认选择「直播录制」。三种模式共用右侧的播放器和操作栏。切换到「yt-dlp」后，在左侧粘贴网页地址并点击「分析链接」，选择格式后可在右侧预览该直链，也可在左侧复制。部分网站会分别提供视频和音频，选项中会标明。直链可能过期，也可能需要原网站的请求头。

在「CCTV」模式粘贴央视网节目页或专辑页地址，点击「解析链接」。从单集入口加载整张专辑时，勾选「从节目页查找所属专辑」。选择分辨率后，右侧「预览链接」播放所选的第一集，「下载视频」按剧集列表顺序下载所有勾选项，并按节目名保存。分辨率由央视公开播放源决定，选择「自动」会取最高可用分辨率。

本地文件与 HTTP(S) 链接的播放格式取决于 Windows 的媒体解码支持。RTSP、RTMP、RTP、UDP 和 SRT 由内置 FFmpeg 转换为可预览的流，通常有数秒缓冲延迟。录制占用同一 RTP 或 UDP 端口时，应用会先释放预览连接。

默认保存到 Windows「视频」中的 `Live Recorder` 文件夹。每次录制使用一个独立的文件夹，默认分片上限为 1 MB（1,000,000 字节）。分片在 MPEG-TS 的 188 字节包边界切开，最后一片可以更小。分片面向顺序合并，单片可能需要前一片的关键帧才能连贯播放。

录制文件夹按本机开始时间命名，如 `record_20261116_210530`。合并后的 MP4 沿用分片文件夹名。同名时依次使用 ` (2)`、` (3)` 等后缀，如 `record_20261116_210530 (2).mp4`。

录制和转码默认会在缩放前处理标记为隔行的画面。如果字幕或移动边缘仍有梳齿，可在「去隔行」中选择「强制去隔行」。已有视频可以用同一设置转码，已有分片可勾选重新编码后再合并。原样合并保留原有画面。

录制和转码可选软件 x264、Intel Quick Sync、NVIDIA NVENC、AMD AMF。硬件编码要求对应的显卡和驱动。原样合并直接复制已录制的音视频流。每个任务保留首路视频和首路音频，纯音频流也可以处理。下载适用于直接媒体链接和 HLS，普通网页播放器地址需先取得媒体链接。

## 从源码构建

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，然后在仓库根目录运行：

```powershell
dotnet build src/LiveRecorder/LiveRecorder.csproj -c Release
dotnet publish src/LiveRecorder/LiveRecorder.csproj -c Release -o artifacts/LiveRecorder-win-x64
```

首次构建时会自动下载并校验固定版本的 FFmpeg、yt-dlp 和 Deno。发布目录包含这些依赖以及 .NET 和 Windows App SDK 运行时。目标机器还需 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。

随包分发的 FFmpeg 是 [Gyan FFmpeg 9.0.2 Essentials](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2) 的 Windows x64 构建，按 GPL v3 授权。发布包的 `ffmpeg` 子目录附带许可证和原始构建说明。对应源码见 [FFmpeg 9.0.2 源码版本](https://github.com/FFmpeg/FFmpeg/tree/946fcce07b)，协议细节见 [FFmpeg 协议文档](https://ffmpeg.org/ffmpeg-protocols.html)。

网页分析使用 [yt-dlp 2026.10.26](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26) 的官方 Windows 可执行文件，以及供其运行网页解析脚本的 [Deno 2.9.8](https://github.com/denoland/deno/releases/tag/v2.9.8)。发布包的 `yt-dlp` 子目录附带源码项目的许可证。yt-dlp 官方打包的可执行文件按 GPL v3 或更高版本授权，GPL v3 全文见 `ffmpeg/LICENSE`。
