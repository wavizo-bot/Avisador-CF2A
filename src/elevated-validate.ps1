param(
    [Parameter(Mandatory = $true)][string]$CertFile,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Continue'

function Log([string]$message) {
    Add-Content -LiteralPath $LogPath -Value ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $message)
}

Set-Content -LiteralPath $LogPath -Value "iniciado $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

try {
    Import-Certificate -FilePath $CertFile -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Log "certificado importado em LocalMachine\TrustedPeople"
}
catch {
    Log "ERRO ao importar certificado: $($_.Exception.Message)"
}

try {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    New-ItemProperty -Path $key -Name AllowAllTrustedApps -PropertyType DWord -Value 1 -Force | Out-Null
    Log "sideload habilitado (AllowAllTrustedApps = 1)"
}
catch {
    Log "ERRO ao habilitar sideload: $($_.Exception.Message)"
}

$appcert = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
if (-not (Test-Path $appcert)) { $appcert = 'C:\Program Files\Windows Kits\10\App Certification Kit\appcert.exe' }

Log "WACK reset"
& $appcert reset 2>&1 | ForEach-Object { Log "reset: $_" }

# o appcert nao sobrescreve relatorio existente
if (Test-Path -LiteralPath $ReportPath) {
    Remove-Item -LiteralPath $ReportPath -Force
    Log "relatorio anterior removido: $ReportPath"
}

Log "WACK test iniciado: $PackagePath"
& $appcert test -appxpackagepath $PackagePath -reportoutputpath $ReportPath 2>&1 | ForEach-Object { Log "test: $_" }
Log "WACK test finalizado (exit=$LASTEXITCODE)"

if (Test-Path $ReportPath) { Log "relatorio gerado: $ReportPath" } else { Log "AVISO: relatorio nao encontrado" }
Log "concluido"
