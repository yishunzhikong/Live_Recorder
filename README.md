# Live Recorder

Live Recorder 是使用 C#、WinUI 3 和 Windows App SDK 构建的 Windows 视频下载和录制工具。支持 Windows 10 2004（19041）及以上版本、Windows 11，提供 x64 构建。

## 使用

1. 解压发布目录，双击 `LiveRecorder.exe`。将支持所需协议与编码器的 **Windows x64 FFmpeg** 放在程序旁边、加入 `PATH`，或在界面的「FFmpeg 路径」中指定 `ffmpeg.exe`。
2. 粘贴链接。对于 HTTP(S) 视频文件，点「下载视频」即可保存原始文件。对于 HLS（M3U/M3U8），应用按播放列表读取分片并自动封装成 MKV。
3. 对于 HLS 直播、RTSP(S)、RTMP(S)、RTP、UDP 或 SRT，点「录制直播」。RTP 动态载荷需要 SDP 描述时，点「从 SDP 录制」选择本地文件。
4. 录制结束或手动停止后，可在弹窗里选择原样合并、用所选编码器重新编码后合并，或保留分片。稍后可通过「合并录制分片」选中该次录制的文件夹。转码与合并均输出 MP4，并保留输入文件。

默认保存到 Windows「视频」中的 `Live Recorder` 文件夹。每次录制使用一个独立的文件夹，默认分片上限为 1 MB（1,000,000 字节）。分片在 MPEG-TS 的 188 字节包边界切开，最后一片可以更小。分片面向顺序合并，单片可能需要前一片的关键帧才能连贯播放。

录制和转码可选软件 x264、Intel Quick Sync、NVIDIA NVENC、AMD AMF。硬件编码要求对应的显卡、驱动和带该编码器的 FFmpeg 构建。原样合并直接复制已录制的音视频流。每个任务保留首路视频和首路音频，纯音频流也可以处理。下载适用于直接媒体链接和 HLS，普通网页播放器地址需先取得媒体链接。

## 从源码构建

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，然后在仓库根目录运行：

```powershell
dotnet build src/LiveRecorder/LiveRecorder.csproj -c Release
dotnet publish src/LiveRecorder/LiveRecorder.csproj -c Release -o artifacts/LiveRecorder-win-x64
```

发布目录包含 .NET 和 Windows App SDK 运行时，FFmpeg 单独提供。目标机器还需 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。

媒体处理基于 [FFmpeg](https://ffmpeg.org/download.html)，协议细节见 [FFmpeg 协议文档](https://ffmpeg.org/ffmpeg-protocols.html)。
