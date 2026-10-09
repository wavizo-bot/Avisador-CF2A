$ErrorActionPreference = 'Stop'

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $src
$out = Join-Path $root 'build\store'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Cap {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

[Win32Cap]::SetProcessDPIAware() | Out-Null

# --- 1) Logo 512x512 para o Store Listing ---
. (Join-Path $src 'make-assets.ps1') *> $null
$logo512 = New-FittedBitmap 512 512
$logo512.Save((Join-Path $out 'logo512.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo512.Dispose()
"logo512.png gerado"

# --- 2) Screenshots da janela do aplicativo ---
$pkg = Get-AppxPackage -Name wavizo.AvisadorCF2A
if (-not $pkg) { throw "Pacote nao instalado." }
$aumid = $pkg.PackageFamilyName + '!AvisadorCF2A'

Get-Process -Name AvisadorCF2A -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# preserva os dados locais do usuario (historico real) fora do perfil usado nas capturas
$udf = Join-Path $env:LOCALAPPDATA 'wavizo\AvisadorCF2A'
$udfBackup = Join-Path $env:LOCALAPPDATA 'wavizo\AvisadorCF2A_backup_dados'
if (Test-Path $udf) {
    if (Test-Path $udfBackup) {
        Remove-Item -Recurse -Force $udf
        "perfil do app reiniciado (backup com dados reais ja existe)"
    }
    else {
        Move-Item -LiteralPath $udf -Destination $udfBackup
        "perfil do app movido para backup (evita dados reais nos screenshots)"
    }
}

(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Process "shell:AppsFolder\$aumid"

$p = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process -Name AvisadorCF2A -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { break }
}
if (-not $p) { throw "Aplicativo nao abriu (sem janela principal)." }
$hwnd = $p.MainWindowHandle
"title: $($p.MainWindowTitle) hwnd=$hwnd"

[Win32Cap]::ShowWindow($hwnd, 3) | Out-Null      # SW_MAXIMIZE
[Win32Cap]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Seconds 6

function Invoke-Click([IntPtr]$h, [int]$rx, [int]$ry) {
    [Win32Cap]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 300
    (New-Object -ComObject Shell.Application).MinimizeAll()
    Start-Sleep -Milliseconds 500
    [Win32Cap]::ShowWindow($h, 3) | Out-Null
    [Win32Cap]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 500
    $r = New-Object Win32Cap+RECT
    [Win32Cap]::GetWindowRect($h, [ref]$r) | Out-Null
    "  click rel=($rx,$ry) abs=($($r.Left + $rx),$($r.Top + $ry)) rect=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
    [Win32Cap]::SetCursorPos(($r.Left + $rx), ($r.Top + $ry)) | Out-Null
    Start-Sleep -Milliseconds 300
    [Win32Cap]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win32Cap]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
}

function Save-WindowShot([IntPtr]$h, [string]$name) {
    [Win32Cap]::SetForegroundWindow($h) | Out-Null
    (New-Object -ComObject Shell.Application).MinimizeAll()
    Start-Sleep -Milliseconds 700
    [Win32Cap]::ShowWindow($h, 3) | Out-Null
    [Win32Cap]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 800

    $r = New-Object Win32Cap+RECT
    [Win32Cap]::GetWindowRect($h, [ref]$r) | Out-Null
    # limita a captura a area util da tela (remove bordas fora da tela e faixa da taskbar)
    $wa = [System.Windows.Forms.Screen]::FromHandle($h).WorkingArea
    $l = [Math]::Max($r.Left, $wa.Left)
    $t = [Math]::Max($r.Top, $wa.Top)
    $rr = [Math]::Min($r.Right, $wa.Right)
    $bb = [Math]::Min($r.Bottom, $wa.Bottom)
    $w = $rr - $l
    $ht = $bb - $t
    $bmp = New-Object System.Drawing.Bitmap($w, $ht, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($l, $t, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
    $g.Dispose()
    $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return "$name -> ${w}x$ht"
}

function Get-ShotTheme([string]$name) {
    $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $out $name))
    $c = $bmp.GetPixel(100, 300)
    $bmp.Dispose()
    if ($c.R -gt 140) { return 'light' } else { return 'dark' }
}

function Capture-Step([IntPtr]$h, [string]$name, [string]$expected) {
    for ($try = 1; $try -le 3; $try++) {
        $size = Save-WindowShot $h $name
        $theme = Get-ShotTheme $name
        if ($theme -eq $expected) {
            "$size tema=$theme [ok]"
            return $true
        }
        "$size tema=$theme esperado=$expected -> alternando tema"
        Invoke-Click $h $posClaro[0] $posClaro[1]
    }
    "$name ATENCAO: tema nao convergiu"
    return $false
}

# posicoes relativas a janela maximizada (1936x1048)
$posTextarea = @(604, 552)
$posGerar    = @(446, 745)
$posClaro    = @(1575, 102)
$posGeradas  = @(1053, 249)
$posHistorico= @(1186, 249)
$posPers     = @(576, 369)
$posLinks    = @(1591, 305)
$posHistoryClear = @(1477, 439)
$posSearch   = @(1155, 361)
$posTermosAceitar = @(1132, 745)

$sample = @(
    'PROFISSIONAL: Maria da Silva',
    '',
    '02-10-2026 15:00',
    'AGENDADO',
    '[CPF:]',
    '1234567',
    'Ana Clara Pereira',
    '[CEL] 91234-5678',
    '',
    '02-10-2026 15:40',
    'AGENDADO',
    '[CPF:]',
    '7654321',
    'Jose Ramos Lima',
    '[RES] 3233-4455',
    '',
    '02-10-2026 16:20',
    'AGENDADO',
    '[CPF:]',
    '2468135',
    'Carla Mendes Rocha',
    '[CEL] 98765-4321'
) -join "`r`n"

# 0) aceita os termos (perfil novo sempre exibe o modal na 1a execucao)
Invoke-Click $hwnd $posTermosAceitar[0] $posTermosAceitar[1]
Start-Sleep -Seconds 1

# 1) tela inicial (perfil novo = tema claro padrao)
Capture-Step $hwnd 'screenshot-01.png' 'light'

# 2) colar dados de exemplo e gerar mensagens
Set-Clipboard -Value $sample
Invoke-Click $hwnd $posTextarea[0] $posTextarea[1]
[System.Windows.Forms.SendKeys]::SendWait('^v')
Start-Sleep -Seconds 2
Invoke-Click $hwnd $posGerar[0] $posGerar[1]
Start-Sleep -Seconds 2
Capture-Step $hwnd 'screenshot-02.png' 'light'

# 3) tema escuro com as mensagens geradas
Invoke-Click $hwnd $posClaro[0] $posClaro[1]
Capture-Step $hwnd 'screenshot-03.png' 'dark'

# 4) aba Historico do Dia (+ limpar busca força re-render do historico)
Invoke-Click $hwnd $posHistorico[0] $posHistorico[1]
Invoke-Click $hwnd $posHistoryClear[0] $posHistoryClear[1]
Capture-Step $hwnd 'screenshot-04.png' 'dark'

# 5) volta para Geradas e expande a personalizacao da mensagem
Invoke-Click $hwnd $posGeradas[0] $posGeradas[1]
Invoke-Click $hwnd $posPers[0] $posPers[1]
Capture-Step $hwnd 'screenshot-05.png' 'dark'

# 6) recolhe a personalizacao e usa a busca para filtrar por nome
Invoke-Click $hwnd $posPers[0] $posPers[1]
Invoke-Click $hwnd $posSearch[0] $posSearch[1]
[System.Windows.Forms.SendKeys]::SendWait('carla')
Start-Sleep -Seconds 2
Capture-Step $hwnd 'screenshot-06.png' 'dark'

Get-Process -Name AvisadorCF2A -ErrorAction SilentlyContinue | Stop-Process -Force

Get-ChildItem $out | Select-Object Name, Length, @{n = 'sha1'; e = { (Get-FileHash $_.FullName -Algorithm SHA1).Hash.Substring(0, 10) } } |
    Format-Table -AutoSize | Out-String -Width 120
