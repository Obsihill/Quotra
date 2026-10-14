<#
.SYNOPSIS
    Build and verify the Fluent header using isolated, deterministic sample data.
.DESCRIPTION
    Exports real WinUI XAML renders, checks opacity/layout and runs the existing
    forecast/parser self-tests. No real account or saved user history is used.
    Screenshots exclude the DWM frame/shadow. Hover coordinates are derived from
    the app's own monitor log, without moving the real mouse.
#>
[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64'
$taskQa = Join-Path ([IO.Path]::GetTempPath()) 'Quotra\verify-fluent'
$taskExe = Join-Path $taskOutput 'QuotraWinUI.exe'
New-Item -ItemType Directory -Path $taskQa -Force | Out-Null

if (-not $SkipBuild) {
    & dotnet build (Join-Path $taskRoot 'src\Quotra.WinUI\Quotra.WinUI.csproj') -c Release -p:Platform=x64 --no-restore -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

function Invoke-Check([string]$Name, [string[]]$Arguments) {
    $taskLog = Join-Path $taskQa "$Name.log"
    $taskErr = Join-Path $taskQa "$Name.err"
    # Start-Process -PassThru 는 Windows PowerShell 5.1 에서 리다이렉트와 함께 쓰면
    # ExitCode 를 돌려주지 않는다(빈 값 — 5.1 의 알려진 특성). ProcessStartInfo 로 직접
    # 띄우면 5.1/7 모두에서 종료 코드·출력이 같은 방식으로 읽힌다.
    $taskInfo = New-Object System.Diagnostics.ProcessStartInfo
    $taskInfo.FileName = $taskExe
    $taskInfo.Arguments = $Arguments -join ' '
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskInfo.RedirectStandardOutput = $true
    $taskInfo.RedirectStandardError = $true
    # 앱은 콘솔 출력을 UTF-8 로 고정한다(Log.cs 의 Boot.Init) — 여기서도 같은 인코딩으로 읽는다.
    $taskInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $taskInfo.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $taskProcess = [System.Diagnostics.Process]::Start($taskInfo)
    # 파이프 버퍼가 차면 자식이 멈춘다 — 기다리는 동안 비동기로 읽어 둔다.
    $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(15000)) {
        $taskProcess.Kill()
        throw "$Name timed out."
    }
    $taskText = $taskStdout.Result
    Set-Content -LiteralPath $taskLog -Value $taskText -Encoding utf8
    Set-Content -LiteralPath $taskErr -Value $taskStderr.Result -Encoding utf8
    if ($taskProcess.ExitCode -ne 0 -or $taskText -match 'UNHANDLED|capture failed') {
        throw "$Name failed. See $taskLog"
    }
    Write-Host "PASS $Name (exit $($taskProcess.ExitCode))"
    return $taskText
}

$taskForecast = Invoke-Check 'forecast' @('--forecast-test')
$taskParser = Invoke-Check 'codex' @('--provider-test=codex')
$taskClaude = Invoke-Check 'claude' @('--provider-test=claude')

# 케이스 수는 고정값 대신 "전부 통과(N/N)"로 본다 — 케이스가 늘어도 게이트가 깨지지 않는다.
# 대신 하한만 지킨다: 케이스가 조용히 줄어들면 실패한다.
function Test-SelfTestAllPassed([string]$Text, [int]$MinimumCases) {
    if ($Text -notmatch '(\d+)/(\d+)\s*통과') { return $false }
    $taskPassed = [int]$Matches[1]
    $taskTotal = [int]$Matches[2]
    return $taskPassed -eq $taskTotal -and $taskTotal -ge $MinimumCases
}

if (-not (Test-SelfTestAllPassed $taskForecast 27) -or -not (Test-SelfTestAllPassed $taskParser 32)) {
    throw 'An existing self-test did not pass (expected all cases passing: forecast >= 27, codex parser >= 32).'
}

$taskProviders = @(
    @{ id='openai'; name='ChatGPT'; accent='#5FD3A8'; primaryLabel='주간' },
    @{ id='anthropic'; name='Claude'; accent='#E08D6B'; primaryLabel='주간' },
    @{ id='xai'; name='Grok'; accent='#D7DCE5'; primaryLabel='주간' }
)
$taskCatalog = Join-Path $taskQa 'providers.json'
$taskSingle = Join-Path $taskQa 'single.json'
$taskEmpty = Join-Path $taskQa 'empty.json'
ConvertTo-Json -InputObject $taskProviders | Set-Content -LiteralPath $taskCatalog -Encoding utf8
ConvertTo-Json -InputObject @($taskProviders[0]) | Set-Content -LiteralPath $taskSingle -Encoding utf8
'[]' | Set-Content -LiteralPath $taskEmpty -Encoding utf8

function New-PreviewArguments([string]$Name, [string]$Theme, [string]$Catalog, [bool]$Simulate = $true) {
    $taskHistoryPath = Join-Path $taskQa "$Name-history.json"
    $taskHistory = @{}
    $taskNow = [DateTimeOffset]::Now
    for ($taskIndex = 0; $taskIndex -lt $taskProviders.Count; $taskIndex++) {
        $taskStart = 0.08 + 0.14 * $taskIndex
        $taskHistory["$($taskProviders[$taskIndex].id)|Primary"] = @(
            @{ at=$taskNow.AddMinutes(-30).ToString('o'); used=$taskStart - 0.06 },
            @{ at=$taskNow.AddMinutes(-20).ToString('o'); used=$taskStart - 0.04 },
            @{ at=$taskNow.AddMinutes(-10).ToString('o'); used=$taskStart - 0.02 }
        )
    }
    ConvertTo-Json -InputObject $taskHistory -Depth 4 | Set-Content -LiteralPath $taskHistoryPath -Encoding utf8
    $taskPng = Join-Path $taskRoot "docs\fluent-$Name.png"
    $taskArgs = @("--theme=$Theme", '--pinned=true', "--catalog=`"$Catalog`"", "--history=`"$taskHistoryPath`"", "--capture=`"$taskPng`"", '--exit-after=6000')
    if ($Simulate) { $taskArgs += '--simulate=12' }
    return $taskArgs
}

$taskDark = Invoke-Check 'dark' ((New-PreviewArguments 'dark' 'dark' $taskCatalog) + '--simpath=0,800@0')
if ($taskDark -notmatch 'capture:.*surface=(transparent\(Acrylic backdrop\)|opaque\(fallback\))' -or $taskDark -notmatch '포커스 유지=True') {
    throw 'Dark surface or focus preservation failed.'
}
# 배치 로그는 여러 번 나온다(로딩 중 빈 상태 → 셀 구성 완료). 호버 대상은 마지막 배치 기준이다.
$taskPlaces = [regex]::Matches($taskDark, 'widget place: 요청=\((-?\d+),(-?\d+)\).*scale=([\d.]+)')
if ($taskPlaces.Count -eq 0) {
    throw 'Could not derive hover coordinates from the app log.'
}
$taskLast = $taskPlaces[$taskPlaces.Count - 1]
$taskScale = [double]::Parse($taskLast.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
$taskHoverX = [int]($taskLast.Groups[1].Value) + [int](365 * $taskScale)
$taskHoverY = [int]($taskLast.Groups[2].Value) + [int](35 * $taskScale)
$taskEdgeY = [int]($taskLast.Groups[2].Value) - [int](14 * $taskScale)
$taskHover = "--simpath=$taskHoverX,$taskHoverY@0"

$taskHoverLog = Invoke-Check 'hover' ((New-PreviewArguments 'hover' 'dark' $taskCatalog) + $taskHover)
$taskLight = Invoke-Check 'light' ((New-PreviewArguments 'light' 'light' $taskCatalog) + $taskHover)
if ($taskHoverLog -notmatch 'hover: Claude → 남음' -or $taskLight -notmatch 'surface=(transparent\(Acrylic backdrop\)|opaque\(fallback\))') {
    throw 'Hover or light theme failed.'
}
$null = Invoke-Check 'single' ((New-PreviewArguments 'single' 'dark' $taskSingle) + '--simpath=0,800@0')
$null = Invoke-Check 'empty' ((New-PreviewArguments 'empty' 'light' $taskEmpty $false) + '--simpath=0,800@0')
$taskUnlinked = Invoke-Check 'unlinked' ((New-PreviewArguments 'unlinked' 'dark' $taskCatalog $false) + '--simpath=0,800@0')
if ($taskUnlinked -match 'widget: 셀 [1-9]') { throw 'Unlinked providers must be hidden.' }
$taskMenu = Invoke-Check 'settings-menu' ((New-PreviewArguments 'settings-menu' 'dark' $taskSingle) + @('--settings-menu-preview', '--simpath=0,800@0'))
if ($taskMenu -notmatch 'settings menu: opened') { throw 'Settings menu did not open.' }
$taskUnpinned = Invoke-Check 'not-topmost' ((New-PreviewArguments 'not-topmost' 'dark' $taskSingle) + @('--simpath=0,800@0', '--ontop=false'))
if ($taskUnpinned -notmatch 'alwaysOnTop=False.*exstyle_topmost=False') {
    throw 'Always-on-top off did not apply.'
}

$taskPopupArgs = @(New-PreviewArguments 'popup' 'dark' $taskCatalog) | Where-Object { $_ -ne '--pinned=true' }
$taskPopupPath = "--simpath=0,800@0;1000,$taskEdgeY@600;1120,$taskEdgeY@800;1000,$taskEdgeY@1000;1120,$taskEdgeY@1200;$taskHoverX,$taskHoverY@1800;0,800@4000"
$taskPopup = Invoke-Check 'popup' ($taskPopupArgs + @('--pinned=false', $taskPopupPath))
if ($taskPopup -notmatch 'popup: 펼침 완료' -or $taskPopup -notmatch 'popup: 숨김 완료') {
    throw 'Edge popup did not open and retract.'
}

Write-Host "Verified forecast and parser self-tests plus nine UI scenarios. QA data/logs: $taskQa"
