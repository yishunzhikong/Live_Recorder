param(
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'BundledTools.props'
$manifest = New-Object System.Xml.XmlDocument
$manifest.PreserveWhitespace = $true
$manifest.Load($manifestPath)
$tools = $manifest.Project.PropertyGroup
$headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
if ($env:GH_TOKEN) {
    $headers.Authorization = 'Bearer ' + $env:GH_TOKEN
}

function Get-LatestTool($Repository, $AssetTemplate) {
    $uri = "https://api.github.com/repos/$Repository/releases/latest"
    try {
        $release = Invoke-RestMethod -Uri $uri -Headers $headers -UserAgent 'LiveRecorder-DependencyCheck' -TimeoutSec 20
    }
    catch {
        throw "Could not query $Repository releases. Check connectivity or set GH_TOKEN if the GitHub API is rate-limited. $($_.Exception.Message)"
    }
    if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Unexpected stable release tag for $Repository."
    }

    $assetName = $AssetTemplate -f $release.tag_name
    $asset = @($release.assets | Where-Object { $_.name -ceq $assetName -and $_.state -eq 'uploaded' })
    if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([a-fA-F0-9]{64})$') {
        throw "No SHA-256 was published for $Repository asset $assetName."
    }
    [pscustomobject]@{ Version = $release.tag_name; Sha256 = $Matches[1].ToLowerInvariant() }
}

$ffmpeg = Get-LatestTool $tools.FfmpegRepository 'ffmpeg-{0}-essentials_build.zip'
$ytDlp = Get-LatestTool $tools.YtDlpRepository 'yt-dlp.exe'
$updates = @(
    @{ Name = 'FFmpeg'; VersionKey = 'FfmpegVersion'; ShaKey = 'FfmpegSha256'; Latest = $ffmpeg }
    @{ Name = 'yt-dlp'; VersionKey = 'YtDlpVersion'; ShaKey = 'YtDlpSha256'; Latest = $ytDlp }
)
$changed = $false
foreach ($update in $updates) {
    $version = $tools.SelectSingleNode($update.VersionKey)
    $checksum = $tools.SelectSingleNode($update.ShaKey)
    $latest = $update.Latest
    if ($version.InnerText -eq $latest.Version) {
        if ($checksum.InnerText -ne $latest.Sha256) {
            throw "$($update.Name) $($latest.Version) has a different upstream checksum. Verify the replaced asset before updating."
        }
        Write-Output "$($update.Name) $($latest.Version) is up to date."
        continue
    }
    if ([version] $latest.Version -lt [version] $version.InnerText) {
        throw "$($update.Name) upstream version $($latest.Version) is older than the pinned version $($version.InnerText)."
    }
    Write-Output "$($update.Name) $($version.InnerText) -> $($latest.Version)"
    $changed = $true
    $version.InnerText = $latest.Version
    $checksum.InnerText = $latest.Sha256
}

if (!$changed) { return }
if ($Check) {
    throw 'Update the bundled tools with build/Update-BundledTools.ps1 before publishing a Release build.'
}

$temporary = "$manifestPath.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $manifest.Save($temporary)
    [IO.File]::Replace($temporary, $manifestPath, [NullString]::Value)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
