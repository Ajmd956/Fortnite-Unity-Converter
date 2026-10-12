$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$app = Split-Path -Parent $here

$dotnetExe = $null
$localDotnet = Join-Path $here '.dotnet'
function Test-Sdk($exe) {
    if (-not $exe) { return $false }
    try {
        foreach ($line in (& $exe --list-sdks)) {
            if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge 10) { return $true }
        }
    } catch {}
    return $false
}
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd -and (Test-Sdk $cmd.Source)) { $dotnetExe = $cmd.Source }
if (-not $dotnetExe) {
    $localExe = Join-Path $localDotnet 'dotnet.exe'
    if (-not (Test-Sdk $localExe)) {
        try {
            $script = Join-Path $env:TEMP 'dotnet-install.ps1'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
            & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 10.0 -InstallDir $localDotnet -NoPath | Out-Null
        } catch {}
    }
    if (Test-Sdk $localExe) { $dotnetExe = $localExe }
}
if (-not $dotnetExe) {
    Write-Output 'The .NET 10 SDK was not found'
    exit 10
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnetExe

$source = Join-Path $here 'CUE4Parse'
if (-not (Test-Path -LiteralPath (Join-Path $source 'CUE4Parse\CUE4Parse.csproj'))) {
    $zip = Join-Path $env:TEMP 'cue4parse_source.zip'
    $extract = Join-Path $env:TEMP 'cue4parse_source'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Invoke-WebRequest -Uri 'https://github.com/h4lfheart/CUE4Parse/archive/2e29798ee69f418c7853a676f3d1415d7ce55422.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $inner = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
    Move-Item -LiteralPath $inner.FullName -Destination $source
    Remove-Item -LiteralPath $zip -Force
    Remove-Item -LiteralPath $extract -Recurse -Force
}

$out = Join-Path $app 'engine\bin'
& $dotnetExe publish (Join-Path $here 'FileLister\FileLister.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:GenerateDocumentationFile=false -p:SatelliteResourceLanguages=en --nologo -v:q -maxcpucount -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { exit 1 }
if (-not (Test-Path -LiteralPath (Join-Path $out 'FortniteFileLister.exe'))) { exit 1 }
exit 0
