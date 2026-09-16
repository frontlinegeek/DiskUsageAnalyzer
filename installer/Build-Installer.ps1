[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string] $Version = '1.1.0',

    [string] $InnoCompiler
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\DiskUsageAnalyzer.App\DiskUsageAnalyzer.App.csproj'
$profilePath = Join-Path $repoRoot 'src\DiskUsageAnalyzer.App\Properties\PublishProfiles\Windows-x64.pubxml'
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$outputDir = Join-Path $repoRoot 'artifacts\installer'
$installerScript = Join-Path $PSScriptRoot 'DiskUsageAnalyzer.iss'

if (-not $InnoCompiler) {
    $compilerCandidates = @(
        $env:INNO_SETUP_COMPILER,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ }

    $InnoCompiler = $compilerCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 was not found. Install it with "winget install --id JRSoftware.InnoSetup --exact", or pass -InnoCompiler with the path to ISCC.exe.'
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $outputDir | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:PublishProfile=$profilePath `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

& $InnoCompiler `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    "/DInstallerOutputDir=$outputDir" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputDir "DiskUsageAnalyzer-$Version-win-x64-setup.exe"
Write-Host "Installer created: $installerPath"
