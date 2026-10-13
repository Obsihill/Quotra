[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [switch]$SkipPublish,
    [switch]$PortableOnly,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repoRoot 'artifacts\release'
$publishDirectory = Join-Path $releaseRoot 'publish\win-x64'
$installerDirectory = Join-Path $releaseRoot 'installer'
$fileVersion = "$Version.0"

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish-release.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Publish step failed.' }
}
$publishedExecutable = Join-Path $publishDirectory 'QuotraWinUI.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable)) {
    throw "Publish output not found: $publishDirectory"
}
if ((Get-Item -LiteralPath $publishedExecutable).VersionInfo.FileVersion.Trim() -ne $fileVersion) {
    throw "Publish version does not match $Version. Publish this version before packaging."
}

function Write-ArtifactChecksum {
    param([string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = "$hash  $([IO.Path]::GetFileName($Path))"
    [IO.File]::WriteAllText("$Path.sha256", "$line`n", [Text.UTF8Encoding]::new($false))
    return $line
}

New-Item -ItemType Directory -Path $releaseRoot, $installerDirectory -Force | Out-Null
$zipPath = Join-Path $releaseRoot "Quotra-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipChecksum = Write-ArtifactChecksum -Path $zipPath

$compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
if ($CompilerPath) {
    $compilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
} elseif ($compiler) {
    $compilerPath = $compiler.Source
} else {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $userPrograms = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\Local\Programs'
    $compilerPath = @(
        (Join-Path $userPrograms 'Inno Setup 7\ISCC.exe'),
        (Join-Path $userPrograms 'Inno Setup 6\ISCC.exe'),
        (Join-Path $programFilesX86 'Inno Setup 7\ISCC.exe'),
        (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $compilerPath) {
    if ($PortableOnly) {
        Write-Host "Created $zipPath (portable ZIP only; Inno Setup was not found)."
        return
    }
    throw 'Inno Setup Compiler (ISCC.exe) is required to create the installer.'
}

$installerScript = Join-Path $repoRoot 'packaging\windows\Quotra.iss'
$compilerArguments = @(
    "/DAppVersion=$Version",
    "/DAppFileVersion=$fileVersion",
    "/DPublishDir=$publishDirectory",
    "/O$installerDirectory",
    $installerScript
)
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$installerPath = Join-Path $installerDirectory "Quotra-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath)) { throw "Installer was not created: $installerPath" }
$installerChecksum = Write-ArtifactChecksum -Path $installerPath
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$zipChecksum`n$installerChecksum`n", [Text.UTF8Encoding]::new($false))
Write-Host "Created $zipPath"
Write-Host "Created $installerPath"
