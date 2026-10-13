[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = 'dotnet'
$sdkVersion = (& $dotnetCommand --version).Trim()
if ($sdkVersion -notmatch '^10\.\d+\.\d+$') {
    $bundledSdk = Join-Path $repoRoot 'artifacts\dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $bundledSdk) {
        $dotnetCommand = $bundledSdk
        $env:DOTNET_ROOT = Split-Path -Parent $bundledSdk
        $sdkVersion = (& $dotnetCommand --version).Trim()
    }
}
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.\d+\.\d+$') {
    throw ".NET 10 안정 SDK가 필요합니다. 현재 SDK: $sdkVersion"
}

$project = Join-Path $repoRoot 'src\Quotra.WinUI\Quotra.WinUI.csproj'
$manifestSource = Join-Path $repoRoot 'src\Quotra.WinUI\app.manifest'
$releaseRoot = Join-Path $repoRoot 'artifacts\release'
$publishDirectory = Join-Path $releaseRoot 'publish\win-x64'
$stagingDirectory = Join-Path $releaseRoot ('staging\' + [Guid]::NewGuid().ToString('N'))
$previousDirectory = Join-Path $releaseRoot ('previous\' + [Guid]::NewGuid().ToString('N'))
$buildOutput = (Join-Path $releaseRoot "build\$Version\bin") + [IO.Path]::DirectorySeparatorChar
$manifestDirectory = Join-Path $releaseRoot 'manifests'
$manifestOutput = Join-Path $manifestDirectory "Quotra-$Version.manifest"
$fileVersion = "$Version.0"

$releaseRootFull = [IO.Path]::GetFullPath($releaseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$publishFull = [IO.Path]::GetFullPath($publishDirectory)
foreach ($targetPath in @($publishFull, [IO.Path]::GetFullPath($stagingDirectory), [IO.Path]::GetFullPath($previousDirectory))) {
    if (-not $targetPath.StartsWith($releaseRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output must stay under $releaseRootFull"
    }
}

New-Item -ItemType Directory -Path $releaseRoot, $manifestDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$manifestText = [IO.File]::ReadAllText($manifestSource)
$manifestText = [regex]::Replace(
    $manifestText,
    '(?<=assemblyIdentity version=")\d+(?:\.\d+){3}(?=")',
    $fileVersion,
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
[IO.File]::WriteAllText($manifestOutput, $manifestText, [Text.UTF8Encoding]::new($false))

$publishArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    "-p:Version=$Version",
    "-p:AssemblyVersion=$fileVersion",
    "-p:FileVersion=$fileVersion",
    "-p:InformationalVersion=$Version",
    "-p:ApplicationManifest=$manifestOutput",
    "-p:BaseOutputPath=$buildOutput",
    '--output', $stagingDirectory
)
if ($NoRestore) { $publishArguments += '--no-restore' }
& $dotnetCommand @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$requiredFiles = @(
    'QuotraWinUI.exe',
    'QuotraWinUI.dll',
    'QuotraWinUI.pri',
    'Assets\Quotra.ico',
    'Data\providers.json'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $relativePath))) {
        throw "Published output is missing required file: $relativePath"
    }
}
if (-not (Get-ChildItem -LiteralPath $stagingDirectory -Filter '*.xbf' -File -Recurse | Select-Object -First 1)) {
    throw 'Published output is missing loose WinUI XBF resources.'
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stagingDirectory 'QuotraWinUI.exe'))
if ($versionInfo.FileVersion -ne $fileVersion) {
    throw "Executable file version mismatch: expected $fileVersion, got $($versionInfo.FileVersion)"
}

# Keep the last working build until publishing and resource checks have succeeded.
if (Test-Path -LiteralPath $publishDirectory) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $previousDirectory) -Force | Out-Null
    Move-Item -LiteralPath $publishDirectory -Destination $previousDirectory
}
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $publishDirectory) -Force | Out-Null
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
} catch {
    if ((Test-Path -LiteralPath $previousDirectory) -and -not (Test-Path -LiteralPath $publishDirectory)) {
        Move-Item -LiteralPath $previousDirectory -Destination $publishDirectory
    }
    throw
}
Write-Host "Published Quotra $Version to $publishDirectory"
