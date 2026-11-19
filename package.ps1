# Packages bin\ for public download: site\downloads\Arabara-<version>.zip, then writes the
# zip's size and SHA-256 into site\index.html.
# Left out on purpose: personal shortcuts (*.lnk) and the Skynet College font, which is
# free for personal use only (see assets\fonts\SkynetCollege-NOTICE.txt). Arabara falls
# back to another display font when it is missing.
# It packages the public release build (build.ps1 -Release, in bin-release) and refuses a
# personal build, which has the video and picture backgrounds (see src\Edition.cs).
param([string]$From = 'bin-release')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root $From
if (-not (Test-Path (Join-Path $bin 'Arabara.exe'))) { throw "$From\Arabara.exe not found; run build.ps1 -Release first." }
$edition = Start-Process -FilePath (Join-Path $bin 'Arabara.exe') -ArgumentList '--edition' -Wait -PassThru
if ($edition.ExitCode -ne 70) { throw "$From\Arabara.exe is not the public release build (run build.ps1 -Release)." }

# Version from the application manifest, e.g. 0.4.2.0 -> 0.4.2
$manifest = Get-Content (Join-Path $root 'src\app.manifest') -Raw
if ($manifest -notmatch 'assemblyIdentity version="(\d+\.\d+\.\d+)') { throw 'Version not found in src\app.manifest.' }
$version = $Matches[1]

$stage = Join-Path ([IO.Path]::GetTempPath()) ("arabara-package-" + [Guid]::NewGuid().ToString('N'))
$folder = Join-Path $stage "Arabara-$version"
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'fonts') | Out-Null
try {
    foreach ($name in 'Arabara.exe', 'Arabara.exe.config', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'WebView2-LICENSE.txt') {
        Copy-Item (Join-Path $bin $name) $folder
    }
    foreach ($name in 'WhiteRabbit.ttf', 'WhiteRabbit-LICENSE.txt', 'WhiteRabbit-README.txt') {
        Copy-Item (Join-Path $bin "fonts\$name") (Join-Path $folder 'fonts')
    }
    $readme = @"
ARABARA $version
Light web browser for Windows 10/11 (64-bit).

Run Arabara.exe. Nothing is installed; your data is kept in %LOCALAPPDATA%\Arabara.
There is no search box: paste a link or type a domain, for example youtube.com.
To search, type google.com and search there.

Needs .NET Framework 4.8 and the Microsoft Edge WebView2 Runtime (both part of
Windows 11 and up-to-date Windows 10).

Windows may say "Windows protected your PC" because Arabara is not code-signed:
choose More info, then Run anyway.

Optional ad blocking: download uBlock Origin Lite's uBOLite_<version>.edge.zip from
https://github.com/uBlockOrigin/uBOL-home/releases and extract it into
%LOCALAPPDATA%\Arabara\extensions\ublock-lite (the folder that holds manifest.json),
then start Arabara. Ads are then blocked everywhere; the Arabara mark at the right of
the address box allows them on the site shown, and blocks them again.

Licences: WebView2-LICENSE.txt (Microsoft WebView2 SDK files),
fonts\WhiteRabbit-LICENSE.txt (White Rabbit font).
"@
    Set-Content -Path (Join-Path $folder 'README.txt') -Value $readme -Encoding UTF8

    $downloads = Join-Path $root 'site\downloads'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $zip = Join-Path $downloads "Arabara-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal

    $files = Get-ChildItem $folder -Recurse -File
    $unpacked = ($files | Measure-Object Length -Sum).Sum
    $packed = (Get-Item $zip).Length
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash
    $sizeText = if ($packed -ge 1MB) { "{0:N1} MB" -f ($packed / 1MB) } else { "{0:N0} KB" -f ($packed / 1KB) }
    $packageText = "Arabara-$version.zip, $sizeText; $($files.Count) files, {0:N1} MB when extracted" -f ($unpacked / 1MB)

    # Update the download page.
    $page = Join-Path $root 'site\index.html'
    $html = [IO.File]::ReadAllText($page)
    $html = [regex]::Replace($html, '<span id="package">[^<]*</span>', "<span id=""package"">$packageText</span>")
    $html = [regex]::Replace($html, '<span id="sha">[^<]*</span>', "<span id=""sha"">$sha</span>")
    [IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding $false))

    "Packaged $zip"
    "  $packageText"
    "  SHA-256 $sha"
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
