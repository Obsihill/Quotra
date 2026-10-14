[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [switch]$RequireTrayRegistration
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$workingDirectory = Split-Path -Parent $executablePath

function Invoke-QuotraProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $process = Start-Process -FilePath $executablePath -ArgumentList $ArgumentList -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Quotra process timed out after $TimeoutSeconds seconds: $($ArgumentList -join ' ')"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        throw "Quotra process failed with exit code $($process.ExitCode): $($ArgumentList -join ' ')"
    }
}

foreach ($argument in @('--forecast-test', '--provider-test=codex', '--provider-test=claude')) {
    Write-Host "Running $argument"
    Invoke-QuotraProcess -ArgumentList @($argument) -TimeoutSeconds 90
}

if ($RequireTrayRegistration) {
    $logPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Quotra\quotra-winui.log'
    $emptyCatalog = Join-Path $PSScriptRoot 'fixtures\empty-providers.json'
    if (-not (Test-Path -LiteralPath $emptyCatalog)) { throw "Empty provider catalog not found: $emptyCatalog" }
    $catalogArgument = '--catalog="' + $emptyCatalog + '"'
    Write-Host 'Launching the app to verify tray registration.'
    Invoke-QuotraProcess -ArgumentList @($catalogArgument, '--exit-after=5000') -TimeoutSeconds 45
    if (-not (Test-Path -LiteralPath $logPath)) {
        throw "Quotra startup log was not created: $logPath"
    }
    $startupLog = [IO.File]::ReadAllText($logPath)
    $logPrefix = '(?:\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] )?'
    if ($startupLog -notmatch ("(?m)^${logPrefix}tray icon: 등록=True(?:\s|$)")) {
        $trayLog = ($startupLog -split "`r?`n" | Where-Object { $_ -match ("^${logPrefix}tray icon:") } | Select-Object -Last 1)
        throw "Quotra did not register its notification-area icon. $trayLog"
    }
}

Write-Host 'Quotra release self-checks passed.'
