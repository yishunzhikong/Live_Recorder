<#
.SYNOPSIS
等待便携应用退出后替换程序文件，并重新启动 Live Recorder。
.PARAMETER ParentProcessId
启动更新的 Live Recorder 进程编号。
.NOTES
更新脚本与新版文件暂存在同一个程序目录中。日志、配置和工具目录保留。
替换过程中发生错误时，尝试恢复已移动的原安装文件。
#>
param([Parameter(Mandatory = $true)][int]$ParentProcessId)

$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath($PSScriptRoot)
$root = [IO.Path]::GetDirectoryName($stage)
if ([IO.Path]::GetFileName($stage) -notmatch '^\.update-[a-f0-9]{32}$' -or
    [IO.Path]::GetPathRoot($root) -eq $root -or
    (([IO.File]::GetAttributes($stage) -bor [IO.File]::GetAttributes($root)) -band [IO.FileAttributes]::ReparsePoint)) {
    throw '更新目录无效。'
}

$exe = [IO.Path]::Combine($root, 'LiveRecorder.exe')
$next = [IO.Path]::Combine($stage, 'next')
$backup = [IO.Path]::Combine($stage, 'previous')
$logDir = [IO.Path]::Combine($root, 'logs')
$preserved = @('logs', 'config', 'tools')
$previous = [System.Collections.Generic.List[string]]::new()
$installed = [System.Collections.Generic.List[string]]::new()
$canRestart = $false
$ownsUpdate = $false
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))) }
finally { $sha256.Dispose() }
$mutex = [Threading.Mutex]::new($false, "Local\LiveRecorder.Update.$hash")

function Move-Entry([string]$Source, [string]$Destination) {
    if ([IO.File]::GetAttributes($Source) -band [IO.FileAttributes]::Directory) {
        [IO.Directory]::Move($Source, $Destination)
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

# 不跟随旧安装目录中可能存在的联接点或符号链接。
function Remove-UpdateTree([string]$Path) {
    $attributes = [IO.File]::GetAttributes($Path)
    if ($attributes -band [IO.FileAttributes]::Directory) {
        if (!($attributes -band [IO.FileAttributes]::ReparsePoint)) {
            foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) { Remove-UpdateTree $entry }
        }
        [IO.Directory]::Delete($Path)
    } else {
        if (!($attributes -band [IO.FileAttributes]::ReparsePoint) -and
            ($attributes -band [IO.FileAttributes]::ReadOnly)) {
            [IO.File]::SetAttributes($Path, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
        }
        [IO.File]::Delete($Path)
    }
}

function Write-UpdateResult([string]$Message) {
    try {
        $null = [IO.Directory]::CreateDirectory($logDir)
        [IO.File]::WriteAllText([IO.Path]::Combine($logDir, 'app-update.log'), "$(Get-Date -Format o) $Message")
    } catch {
        # 日志写入失败不影响已经完成的程序替换或恢复。
    }
}

try {
    try { $ownsUpdate = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsUpdate = $true }
    if (!$ownsUpdate) { throw '此目录已有应用更新正在安装。' }

    $parent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
    if ($parent) {
        if ($parent.Path -ne $exe) { throw '更新进程与当前程序路径不一致。' }
        if (!$parent.WaitForExit(30000)) { throw 'Live Recorder 未能在规定时间内退出。' }
    }
    $other = Get-Process -Name LiveRecorder -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($other) { throw '请关闭此目录中其他正在运行的 Live Recorder 窗口后再更新。' }
    $canRestart = $true

    if (![IO.File]::Exists($exe) -or ![IO.File]::Exists([IO.Path]::Combine($next, 'LiveRecorder.exe'))) {
        throw '新版或当前程序不完整。'
    }
    $nextEntries = [IO.Directory]::GetFileSystemEntries($next)
    foreach ($entry in $nextEntries) {
        if ([IO.Path]::GetFileName($entry) -in $preserved) { throw '新版安装包包含保留目录。' }
    }
    $null = [IO.Directory]::CreateDirectory($backup)
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($root)) {
        $name = [IO.Path]::GetFileName($entry)
        if ($name -in $preserved -or $entry -eq $stage) { continue }
        Move-Entry $entry ([IO.Path]::Combine($backup, $name))
        $previous.Add($name)
    }
    foreach ($entry in $nextEntries) {
        $name = [IO.Path]::GetFileName($entry)
        Move-Entry $entry ([IO.Path]::Combine($root, $name))
        $installed.Add($name)
    }
    Start-Process -FilePath $exe -WorkingDirectory $root
    Write-UpdateResult '更新成功。'
    try { Remove-UpdateTree $stage }
    catch { Write-UpdateResult "更新成功，无法清理暂存目录：$($_.Exception.Message)" }
} catch {
    $message = $_.Exception.Message
    $restored = $true
    foreach ($name in $installed) {
        try { Move-Entry ([IO.Path]::Combine($root, $name)) ([IO.Path]::Combine($next, $name)) }
        catch { $restored = $false }
    }
    foreach ($name in $previous) {
        try { Move-Entry ([IO.Path]::Combine($backup, $name)) ([IO.Path]::Combine($root, $name)) }
        catch { $restored = $false }
    }
    if (!$restored) { $message += "`n部分旧文件仍保存在 $backup。" }
    Write-UpdateResult "更新失败：$message"
    if ($restored -and $canRestart -and [IO.File]::Exists($exe)) {
        try { Start-Process -FilePath $exe -WorkingDirectory $root } catch { }
    }
    Add-Type -AssemblyName System.Windows.Forms
    $null = [System.Windows.Forms.MessageBox]::Show($message, 'Live Recorder 更新失败')
    exit 1
} finally {
    if ($ownsUpdate) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
