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
        [int]$TimeoutSeconds,
        [switch]$CaptureOutput
    )

    $checkOutput = Join-Path ([IO.Path]::GetTempPath()) ("Quotra-check-" + [Guid]::NewGuid().ToString('N') + '.log')
    # Redirecting output also launches directly instead of through the Windows shell.
    $process = Start-Process -FilePath $executablePath -ArgumentList $ArgumentList -WorkingDirectory $workingDirectory -WindowStyle Hidden -RedirectStandardOutput $checkOutput -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Quotra process timed out after $TimeoutSeconds seconds: $($ArgumentList -join ' ')"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $checkOutput -Tail 12 | Write-Host
        throw "Quotra process failed with exit code $($process.ExitCode): $($ArgumentList -join ' ')"
    }
    $checkText = [IO.File]::ReadAllText($checkOutput)
    $checkText -split "`r?`n" | Select-String -Pattern 'self-test total:|통과|passed' | ForEach-Object { Write-Host $_.Line }
    Remove-Item -LiteralPath $checkOutput -Force
    if ($CaptureOutput) { return $checkText }
}

foreach ($argument in @('--forecast-test', '--provider-test=codex', '--provider-test=claude', '--provider-test=commandcode')) {
    Write-Host "Running $argument"
    Invoke-QuotraProcess -ArgumentList @($argument) -TimeoutSeconds 90
}

if ($RequireTrayRegistration) {
    $emptyCatalog = Join-Path $PSScriptRoot 'fixtures\empty-providers.json'
    if (-not (Test-Path -LiteralPath $emptyCatalog)) { throw "Empty provider catalog not found: $emptyCatalog" }
    $catalogArgument = '--catalog="' + $emptyCatalog + '"'
    Write-Host 'Launching the app to verify tray registration.'
    $startupLog = Invoke-QuotraProcess -ArgumentList @($catalogArgument, '--exit-after=5000') -TimeoutSeconds 45 -CaptureOutput
    $logPrefix = '(?:\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] )?'
    if ($startupLog -notmatch ("(?m)^${logPrefix}tray icon: 등록=True(?:\s|$)")) {
        $trayLog = ($startupLog -split "`r?`n" | Where-Object { $_ -match ("^${logPrefix}tray icon:") } | Select-Object -Last 1)
        throw "Quotra did not register its notification-area icon. $trayLog"
    }
}

Write-Host 'Quotra release self-checks passed.'
