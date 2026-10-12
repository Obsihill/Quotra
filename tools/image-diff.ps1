# image-diff.ps1
# Compares two PNG snapshots pixel by pixel (pure refactor verification).
# Prints: size match, differing pixel count, max per-channel delta, diff bounding box.
# Usage:
#   pwsh -File tools\image-diff.ps1 -A baseline.png -B after.png
#   pwsh -File tools\image-diff.ps1 -A baseline.png -B after.png -DiffImage diff.png
param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [int]$Tolerance = 0,
    [string]$DiffImage
)

Add-Type -AssemblyName System.Drawing

function Get-Bytes([string]$path) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $info = [pscustomobject]@{ Width = $bmp.Width; Height = $bmp.Height; Stride = $data.Stride; Bytes = $bytes }
    $bmp.Dispose()
    return $info
}

$ia = Get-Bytes $A
$ib = Get-Bytes $B

Write-Output ("A: {0} ({1}x{2})" -f (Split-Path $A -Leaf), $ia.Width, $ia.Height)
Write-Output ("B: {0} ({1}x{2})" -f (Split-Path $B -Leaf), $ib.Width, $ib.Height)

if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) {
    Write-Output "RESULT: SIZE MISMATCH"
    exit 1
}

$diffPixels = 0
$maxDelta = 0
$minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1
$diffMask = $null
if ($DiffImage) { $diffMask = New-Object byte[] ($ia.Stride * $ia.Height) }

for ($y = 0; $y -lt $ia.Height; $y++) {
    $rowA = $y * $ia.Stride
    for ($x = 0; $x -lt $ia.Width; $x++) {
        $i = $rowA + $x * 4
        $d = 0
        for ($c = 0; $c -lt 4; $c++) {
            $delta = [Math]::Abs([int]$ia.Bytes[$i + $c] - [int]$ib.Bytes[$i + $c])
            if ($delta -gt $d) { $d = $delta }
        }

        if ($d -gt $Tolerance) {
            $diffPixels++
            if ($d -gt $maxDelta) { $maxDelta = $d }
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
            if ($diffMask) {
                $diffMask[$i + 0] = 255; $diffMask[$i + 1] = 0; $diffMask[$i + 2] = 255; $diffMask[$i + 3] = 255
            }
        }
        elseif ($diffMask) {
            $g = [byte]([int]$ib.Bytes[$i] * 0.35)
            $diffMask[$i + 0] = $g; $diffMask[$i + 1] = $g; $diffMask[$i + 2] = $g; $diffMask[$i + 3] = 255
        }
    }
}

$total = $ia.Width * $ia.Height
Write-Output ("tolerance   : {0}" -f $Tolerance)
Write-Output ("diff pixels : {0} / {1} ({2:N4}%)" -f $diffPixels, $total, (100.0 * $diffPixels / $total))
Write-Output ("max delta   : {0}" -f $maxDelta)
if ($diffPixels -gt 0) {
    Write-Output ("diff bbox   : x={0}..{1} y={2}..{3}" -f $minX, $maxX, $minY, $maxY)
}

if ($DiffImage) {
    $bmp = New-Object System.Drawing.Bitmap $ia.Width, $ia.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $ia.Width, $ia.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    [System.Runtime.InteropServices.Marshal]::Copy($diffMask, 0, $data.Scan0, $diffMask.Length)
    $bmp.UnlockBits($data)
    $bmp.Save($DiffImage, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("diff image  : {0}" -f $DiffImage)
}

if ($diffPixels -eq 0) { Write-Output "RESULT: IDENTICAL" } else { Write-Output "RESULT: DIFFERENT" }
