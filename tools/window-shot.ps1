<#
.SYNOPSIS
    실행 중인 QuotraWinUI 위젯 창을 PNG 로 저장한다(검증용).

.DESCRIPTION
    WinUI 3 창은 WS_EX_NOREDIRECTIONBITMAP 로 만들어져서 GDI 화면 캡처
    (CopyFromScreen / BitBlt)에 잡히지 않는다. 실제로 잡히는 것은 뒤에 있는 창이다.
    그래서 PrintWindow(PW_RENDERFULLCONTENT) 로 창 자신의 내용을 그려서 저장한다.

.PARAMETER Out
    저장할 PNG 경로(상대 경로는 현재 디렉터리 기준).

.PARAMETER Scale
    출력 배율. 모니터가 150% 면 1.5 로 주면 물리 픽셀 크기에 가깝게 저장된다(기본 1.5).

.PARAMETER ProcessName
    대상 프로세스 이름(기본 QuotraWinUI).

.EXAMPLE
    .\tools\window-shot.ps1 -Out docs\winui-expanded.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [double]$Scale = 1.5,
    [string]$ProcessName = 'QuotraWinUI'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('QuotraShot' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class QuotraShot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1
if (-not $proc) { throw "$ProcessName 프로세스(창 있음)를 찾지 못했습니다. 먼저 실행하세요." }

$hwnd = $proc.MainWindowHandle
if (-not [QuotraShot]::IsWindowVisible($hwnd)) { throw "창이 보이지 않는 상태입니다(hwnd=0x$('{0:X}' -f [int64]$hwnd))." }

$rect = New-Object QuotraShot+RECT
[QuotraShot]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = [int][math]::Round(($rect.Right - $rect.Left) * $Scale)
$height = [int][math]::Round(($rect.Bottom - $rect.Top) * $Scale)
if ($width -le 0 -or $height -le 0) { throw "창 크기를 읽지 못했습니다." }

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    $ok = [QuotraShot]::PrintWindow($hwnd, $hdc, 2)
} finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

if (-not $ok) { Write-Warning "PrintWindow 가 실패를 반환했습니다(빈 이미지일 수 있음)." }
Write-Host "저장: $Out  (hwnd=0x$('{0:X}' -f [int64]$hwnd), 창 $($rect.Right - $rect.Left)x$($rect.Bottom - $rect.Top) 논리 → $width x $height px)"
