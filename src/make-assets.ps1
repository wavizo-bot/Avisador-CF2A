$ErrorActionPreference = 'Stop'

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path $src 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Add-Type -AssemblyName System.Drawing

$bg = [System.Drawing.Color]::FromArgb(255, 0x2A, 0x52, 0x98)

$logoPath = Join-Path $src 'logo.jpg'
if (-not (Test-Path -LiteralPath $logoPath)) { throw "Logo nao encontrado: $logoPath" }
$script:logoImg = [System.Drawing.Image]::FromFile($logoPath)

function New-FittedBitmap([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear($bg)

    try {
        $pad = 0.94
        $scale = [Math]::Min(($w * $pad) / $script:logoImg.Width, ($h * $pad) / $script:logoImg.Height)
        $dw = [int][Math]::Round($script:logoImg.Width * $scale)
        $dh = [int][Math]::Round($script:logoImg.Height * $scale)
        $x = [int][Math]::Round(($w - $dw) / 2)
        $y = [int][Math]::Round(($h - $dh) / 2)
        $g.DrawImage($script:logoImg, (New-Object System.Drawing.Rectangle($x, $y, $dw, $dh)))
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Get-BmpEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int32]0)
    $bw.Write([int32]($w * $h * 4))
    $bw.Write([int32]0)
    $bw.Write([int32]0)
    $bw.Write([int32]0)
    $bw.Write([int32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buffer = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
    $bmp.UnlockBits($data)

    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($buffer, $y * $stride, $w * 4)
    }

    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    $bmp.Dispose()
    return $ms.ToArray()
}

function Get-PngEntry([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

Save-Png (New-FittedBitmap 44 44)   (Join-Path $assets 'logo44.png')
Save-Png (New-FittedBitmap 50 50)   (Join-Path $assets 'storelogo.png')
Save-Png (New-FittedBitmap 150 150) (Join-Path $assets 'logo150.png')
Save-Png (New-FittedBitmap 310 310) (Join-Path $assets 'logo310.png')
Save-Png (New-FittedBitmap 310 150) (Join-Path $assets 'logowide.png')
Save-Png (New-FittedBitmap 620 300) (Join-Path $assets 'splash.png')

$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($s in $iconSizes) {
    $bmp = New-FittedBitmap $s $s
    if ($s -ge 256) {
        $entries += , @{ size = $s; data = (Get-PngEntry $bmp) }
    }
    else {
        $entries += , @{ size = $s; data = (Get-BmpEntry $bmp) }
    }
}

$icoPath = Join-Path $src 'app.ico'
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$entries.Count)
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    $dim = if ($e.size -ge 256) { 0 } else { $e.size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$e.data.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.data.Length
}
foreach ($e in $entries) {
    $bw.Write([byte[]]$e.data)
}
$bw.Flush()
[IO.File]::WriteAllBytes($icoPath, $ms.ToArray())

Get-ChildItem $assets | Select-Object Name, Length | Format-Table -AutoSize | Out-String -Width 120
"ico: $((Get-Item $icoPath).Length) bytes"
