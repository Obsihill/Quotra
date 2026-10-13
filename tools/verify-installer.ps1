[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Installer,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Installer smoke verification runs only on an isolated GitHub Actions Windows runner.'
}

$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = Join-Path $runnerTemp ("Quotra-installer-smoke-" + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'install'
$executablePath = Join-Path $installDirectory 'QuotraWinUI.exe'
$uninstallerPath = Join-Path $installDirectory 'unins000.exe'
$programsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$shortcutPath = Join-Path $programsDirectory 'Quotra.lnk'
$appDataDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Quotra'
$markerPath = Join-Path $appDataDirectory ("installer-smoke-" + [Guid]::NewGuid().ToString('N') + '.txt')
$markerCreated = $false

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installDirectory`"")

    Write-Host 'Installing Quotra for the first time.'
    & $installerPath @installArguments
    if ($LASTEXITCODE -ne 0) { throw "Initial installation failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Installed executable was not found.' }
    if (-not (Test-Path -LiteralPath $shortcutPath)) { throw 'Start Menu shortcut was not created.' }

    $appFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath).FileVersion
    $expectedFileVersion = "$Version.0"
    if ($appFileVersion -ne $expectedFileVersion) {
        throw "Installed executable version mismatch: expected $expectedFileVersion, got $appFileVersion."
    }

    $emptyCatalog = Join-Path $PSScriptRoot 'fixtures\empty-providers.json'
    if (-not (Test-Path -LiteralPath $emptyCatalog)) { throw "Empty provider catalog not found: $emptyCatalog" }
    $catalogArgument = '--catalog="' + $emptyCatalog + '"'
    $process = Start-Process -FilePath $executablePath -ArgumentList @($catalogArgument, '--exit-after=5000') -WorkingDirectory $installDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'Installed app startup timed out after 45 seconds.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Installed app startup failed with exit code $($process.ExitCode)." }
    $logPath = Join-Path $appDataDirectory 'quotra-winui.log'
    if (-not (Test-Path -LiteralPath $logPath)) { throw 'Installed app did not create its user log.' }
    $startupLog = [IO.File]::ReadAllText($logPath)
    if ($startupLog -notmatch '(?m)^tray icon: 등록=True(?:\s|$)') { throw 'Installed app failed to register its tray icon.' }

    New-Item -ItemType Directory -Path $appDataDirectory -Force | Out-Null
    Set-Content -LiteralPath $markerPath -Value 'preserve user data across upgrade and uninstall' -Encoding utf8
    $markerCreated = $true

    Write-Host 'Installing over the existing app to verify upgrade behavior.'
    & $installerPath @installArguments
    if ($LASTEXITCODE -ne 0) { throw "Upgrade installation failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Executable was missing after upgrade.' }
    if (-not (Test-Path -LiteralPath $shortcutPath)) { throw 'Start Menu shortcut was missing after upgrade.' }
    if (-not (Test-Path -LiteralPath $markerPath)) { throw 'User data was removed during upgrade.' }

    Write-Host 'Uninstalling Quotra to verify cleanup and data preservation.'
    & $uninstallerPath /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
    if ($LASTEXITCODE -ne 0) { throw "Uninstall failed with exit code $LASTEXITCODE." }
    if (Test-Path -LiteralPath $executablePath) { throw 'Application files remained after uninstall.' }
    if (Test-Path -LiteralPath $shortcutPath) { throw 'Start Menu shortcut remained after uninstall.' }
    if (-not (Test-Path -LiteralPath $markerPath)) { throw 'User data was removed during uninstall.' }

    Write-Host "Installer smoke checks passed (Quotra $Version)."
}
finally {
    if (Test-Path -LiteralPath $uninstallerPath) {
        & $uninstallerPath /VERYSILENT /SUPPRESSMSGBOXES /NORESTART | Out-Null
    }
    if ($markerCreated -and (Test-Path -LiteralPath $markerPath)) {
        Remove-Item -LiteralPath $markerPath -Force
    }

    $testRootFull = [IO.Path]::GetFullPath($testRoot)
    if (-not $testRootFull.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove installer smoke files outside RUNNER_TEMP: $testRootFull"
    }
    if (Test-Path -LiteralPath $testRootFull) {
        Remove-Item -LiteralPath $testRootFull -Recurse -Force
    }
}
