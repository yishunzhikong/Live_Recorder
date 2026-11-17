param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$tools = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BundledTools.props') -Raw)).Project.PropertyGroup
$version = $tools.FfmpegVersion
$archiveName = "ffmpeg-$version-essentials_build"
$url = "https://github.com/$($tools.FfmpegRepository)/releases/download/$version/$archiveName.zip"
$checksum = $tools.FfmpegSha256
$files = @{
    'bin/ffmpeg.exe' = 'ffmpeg.exe'
    'LICENSE' = 'LICENSE'
    'README.txt' = 'README.txt'
}

[void](New-Item -ItemType Directory -Path $Destination -Force)
$staging = Join-Path $Destination ([guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $staging)
try {
    $archive = Join-Path $staging "$archiveName.zip"
    & curl.exe --fail --location --silent --show-error --retry 2 --output $archive $url
    if ($LASTEXITCODE -ne 0) {
        throw "Could not download FFmpeg $version (curl exit code $LASTEXITCODE)."
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $checksum) {
        throw 'FFmpeg archive failed SHA-256 verification.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($path in $files.Keys) {
            $entry = $zip.GetEntry("$archiveName/$path")
            if ($null -eq $entry) {
                throw "FFmpeg archive is missing $path."
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $files[$path]))
        }
    }
    finally {
        $zip.Dispose()
    }

    foreach ($file in $files.Values) {
        $target = Join-Path $Destination $file
        try {
            [IO.File]::Move((Join-Path $staging $file), $target)
        }
        catch [IO.IOException] {
            if (-not [IO.File]::Exists($target)) { throw }
        }
    }
}
finally {
    Remove-Item -LiteralPath (Join-Path $staging "$archiveName.zip"), (Join-Path $staging 'ffmpeg.exe'),
        (Join-Path $staging 'LICENSE'), (Join-Path $staging 'README.txt') -ErrorAction SilentlyContinue
    [IO.Directory]::Delete($staging)
}
