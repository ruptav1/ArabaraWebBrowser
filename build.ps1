# -Release builds the public download (see src\Edition.cs) into bin-release unless told otherwise.
param([switch]$Test, [string]$OutputDirectory = 'bin', [switch]$Release)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$sdkVersion = '1.0.4258.31'
$sdkHash = '56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9'
$sdkDir = Join-Path $PSScriptRoot ".packages\webview2.$sdkVersion"
$sdkArchive = Join-Path $PSScriptRoot ".packages\quarantine\webview2.$sdkVersion.zip"
if (-not (Test-Path -LiteralPath $sdkArchive)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $sdkArchive) | Out-Null
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdkVersion/microsoft.web.webview2.$sdkVersion.nupkg" -OutFile $sdkArchive
}
if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA256).Hash -ne $sdkHash) {
    throw 'WebView2 package checksum does not match the reviewed package.'
}
if (-not (Test-Path -LiteralPath $sdkDir)) {
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkDir
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 is required.' }
if ($Release -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) { $OutputDirectory = 'bin-release' }
$defines = @()
if ($Release) { $defines += '/define:PUBLIC_RELEASE' }
$outputDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
if (-not $outputDir.StartsWith($PSScriptRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build output must be a subfolder of this project.'
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$exePath = Join-Path $outputDir 'Arabara.exe'
$fontHashes = @{
    'WhiteRabbit.ttf' = '3E845AF724F2916D7DB7A0565C52C0FCFB0D57ED9615D3640707C6EEB5B1CAF7'
    'Skynet College.ttf' = 'D1A0E555C1EF4535EFBF0D7057E1342B402C5B30EEB3610F5B72AE4DB8D15983'
}
foreach ($fontName in $fontHashes.Keys) {
    $fontPath = Join-Path $PSScriptRoot ('assets\fonts\' + $fontName)
    # Skynet College is free for personal use only and is not in the repository; Arabara falls
    # back to another display font without it.
    if ($fontName -eq 'Skynet College.ttf' -and -not (Test-Path -LiteralPath $fontPath)) { continue }
    if ((Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash -ne $fontHashes[$fontName]) {
        throw ('Font checksum does not match the reviewed asset: ' + $fontName)
    }
}
& (Join-Path $PSScriptRoot 'assets\build-icon.ps1')
$coreDll = Join-Path $sdkDir 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$formsDll = Join-Path $sdkDir 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$sources = @(Get-ChildItem -LiteralPath 'src' -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /warn:4 @defines "/out:$exePath" /win32manifest:src\app.manifest /win32icon:assets\arabara.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/r:$coreDll" "/r:$formsDll" $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath $coreDll,$formsDll -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $sdkDir 'build\native\x64\WebView2Loader.dll') -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $sdkDir 'LICENSE.txt') -Destination (Join-Path $outputDir 'WebView2-LICENSE.txt') -Force
Copy-Item -LiteralPath 'src\App.config' -Destination (Join-Path $outputDir 'Arabara.exe.config') -Force
$fontOutputDir = Join-Path $outputDir 'fonts'
New-Item -ItemType Directory -Force -Path $fontOutputDir | Out-Null
foreach ($fontFile in @('WhiteRabbit.ttf','WhiteRabbit-LICENSE.txt','WhiteRabbit-README.txt','Skynet College.ttf','SkynetCollege-NOTICE.txt')) {
    if ($fontFile -like 'Skynet*' -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot ('assets\fonts\' + $fontFile)))) { continue }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('assets\fonts\' + $fontFile)) -Destination $fontOutputDir -Force
}
Write-Host ("Built $exePath (Windows x64" + $(if ($Release) { ', public release' } else { '' }) + ")")
if ($Test) {
    $testProcess = Start-Process -FilePath $exePath -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path (Split-Path $outputDir) 'test-results\unit-tests.txt')
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
