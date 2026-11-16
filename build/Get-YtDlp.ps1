param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$ytDlpVersion = '2026.10.26'
$denoVersion = 'v2.9.8'
$ytDlpRelease = "https://github.com/yt-dlp/yt-dlp/releases/download/$ytDlpVersion"
$denoRelease = "https://github.com/denoland/deno/releases/download/$denoVersion"

function Download($Url, $Target, $Checksum) {
    & curl.exe --fail --location --silent --show-error --retry 2 --output $Target $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed for $Url (curl exit code $LASTEXITCODE)." }
    if ($Checksum -and (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash -ne $Checksum) {
        throw "SHA-256 verification failed for $Url."
    }
}

[void](New-Item -ItemType Directory -Path $Destination -Force)
$staging = Join-Path $Destination ([guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $staging)
$denoArchive = Join-Path $staging 'deno.zip'
$files = @('yt-dlp.exe', 'deno.exe', 'LICENSE-yt-dlp.txt', 'LICENSE-deno.txt')
try {
    Download "$ytDlpRelease/yt-dlp.exe" (Join-Path $staging 'yt-dlp.exe') 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
    Download "$denoRelease/deno-x86_64-pc-windows-msvc.zip" $denoArchive 'cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf'
    Download "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$ytDlpVersion/LICENSE" (Join-Path $staging 'LICENSE-yt-dlp.txt')
    Download "https://raw.githubusercontent.com/denoland/deno/$denoVersion/LICENSE.md" (Join-Path $staging 'LICENSE-deno.txt')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($denoArchive)
    try {
        $entry = $zip.GetEntry('deno.exe')
        if ($null -eq $entry) { throw 'Deno archive is missing deno.exe.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging 'deno.exe'))
    }
    finally { $zip.Dispose() }

    foreach ($file in $files) {
        $target = Join-Path $Destination $file
        try { [IO.File]::Move((Join-Path $staging $file), $target) }
        catch [IO.IOException] { if (-not [IO.File]::Exists($target)) { throw } }
    }
}
finally {
    foreach ($file in ($files + 'deno.zip')) {
        Remove-Item -LiteralPath (Join-Path $staging $file) -ErrorAction SilentlyContinue
    }
    [IO.Directory]::Delete($staging)
}
