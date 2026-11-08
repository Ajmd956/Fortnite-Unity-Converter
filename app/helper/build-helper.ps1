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

$cuePin = '8fc9a4ddb1a0b89fe33e283a004b99a6faa46c41'
$source = Join-Path $here 'CUE4Parse'
$pinFile = Join-Path $source '.pinned-commit'
$haveSource = Test-Path -LiteralPath (Join-Path $source 'CUE4Parse\CUE4Parse.csproj')
$havePin = $haveSource -and (Test-Path -LiteralPath $pinFile) -and (([System.IO.File]::ReadAllText($pinFile)).Trim() -eq $cuePin)
if (-not $havePin) {
    $zip = Join-Path $env:TEMP 'cue4parse_source.zip'
    $extract = Join-Path $env:TEMP 'cue4parse_source'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Invoke-WebRequest -Uri "https://github.com/h4lfheart/CUE4Parse/archive/$cuePin.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $inner = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
    Move-Item -LiteralPath $inner.FullName -Destination $source
    [System.IO.File]::WriteAllText($pinFile, $cuePin)
    Remove-Item -LiteralPath $zip -Force
    Remove-Item -LiteralPath $extract -Recurse -Force
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$skeletalSource = Join-Path $source 'CUE4Parse\UE4\Assets\Exports\SkeletalMesh\USkeletalMesh.cs'
if (Test-Path -LiteralPath $skeletalSource) {
    $skeletalText = [System.IO.File]::ReadAllText($skeletalSource)
    $naniteRead = 'NaniteResources = new FNaniteResources(Ar);'
    $naniteSafe = 'try { NaniteResources = new FNaniteResources(Ar); } catch (Exception) { NaniteResources = null; }'
    if ($skeletalText.Contains($naniteRead) -and -not $skeletalText.Contains($naniteSafe)) {
        $skeletalText = $skeletalText.Replace($naniteRead, $naniteSafe)
    }
    $lodRead = 'LODModels = Ar.ReadArray(() => new FStaticLODModel(Ar, bHasVertexColors));'
    $lodTrace = '{ var lodCount = Ar.Read<int>(); LODModels = new FStaticLODModel[lodCount]; for (var lodIndex = 0; lodIndex < lodCount; lodIndex++) { var lodStart = Ar.Position; try { LODModels[lodIndex] = new FStaticLODModel(Ar, bHasVertexColors); } catch (Exception lodError) { throw new InvalidDataException("LOD " + lodIndex + " of " + lodCount + " failed (game=" + Ar.Game + ", started at " + lodStart + ", stopped at " + Ar.Position + ", file size " + Ar.Length + "): " + lodError.GetType().Name + ": " + lodError.Message + " | " + string.Join(" <- ", (lodError.StackTrace ?? "").Split((char) 10).Take(8).Select(x => x.Trim())), lodError); } } }'
    if ($skeletalText.Contains($lodRead) -and -not $skeletalText.Contains('lodStart')) {
        $skeletalText = $skeletalText.Replace($lodRead, $lodTrace)
    }
    [System.IO.File]::WriteAllText($skeletalSource, $skeletalText, $utf8)
}

$lodSource = Join-Path $source 'CUE4Parse\UE4\Assets\Exports\SkeletalMesh\FStaticLODModel.cs'
if (Test-Path -LiteralPath $lodSource) {
    $lodText = [System.IO.File]::ReadAllText($lodSource)
    $vertLine = 'VertexBufferGPUSkin.VertsFloat = new FGPUVertFloat[NumVertices];'
    $vertCheck = 'if (positionVertexBuffer.Verts.Length < NumVertices || skinWeightVertexBuffer.Weights.Length < NumVertices || staticMeshVertexBuffer.UV.Length < NumVertices) throw new InvalidDataException("vertex buffers do not match: NumVertices=" + NumVertices + " positions=" + positionVertexBuffer.Verts.Length + " skinWeights=" + skinWeightVertexBuffer.Weights.Length + " uvs=" + staticMeshVertexBuffer.UV.Length + " texCoords=" + staticMeshVertexBuffer.NumTexCoords);' + "`n" + '        ' + $vertLine
    if ($lodText.Contains($vertLine) -and -not $lodText.Contains('vertex buffers do not match')) {
        [System.IO.File]::WriteAllText($lodSource, $lodText.Replace($vertLine, $vertCheck), $utf8)
    }
}

$out = Join-Path $app 'engine\bin'
& $dotnetExe publish (Join-Path $here 'FileLister\FileLister.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:GenerateDocumentationFile=false -p:SatelliteResourceLanguages=en --nologo -v:q -maxcpucount -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { exit 1 }
if (-not (Test-Path -LiteralPath (Join-Path $out 'FortniteFileLister.exe'))) { exit 1 }
exit 0
