param(
    [ValidateSet('x64', 'x86', 'all')]
    [string]$Arch = 'all',

    [switch]$Sign,

    [switch]$SkipAssets
)

$ErrorActionPreference = 'Stop'

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $src
$build = Join-Path $root 'build'

$identityName = 'wavizo.AvisadorCF2A'
$publisher = 'CN=57BB464E-553F-45B6-A4ED-B253157408EB'
$version = '1.1.0.0'
$exeName = 'AvisadorCF2A.exe'
$htmlSource = Join-Path $root 'Package\html\FullAutoMensagensWhatsapp_v7.html'

function Find-KitsTool([string]$name) {
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kits) {
        $hits = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' }
        if ($hits) {
            $best = $hits | Sort-Object { 
                if ($_.Directory.Name -match '^\d+\.\d+\.\d+\.\d+$') { [version]$_.Directory.Name } else { [version]'0.0' }
            } -Descending | Select-Object -First 1
            return $best.FullName
        }
    }
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Ferramenta nao encontrada: $name"
}

function Get-Manifest([string]$arch) {
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity Name="$identityName"
            Publisher="$publisher"
            Version="$version"
            ProcessorArchitecture="$arch" />

  <Properties>
    <DisplayName>Avisador CF2A</DisplayName>
    <Description>Aplicativo de consulta CF2A para Cl&#237;nica da Fam&#237;lia II</Description>
    <PublisherDisplayName>wavizo</PublisherDisplayName>
    <Logo>assets\storelogo.png</Logo>
  </Properties>

  <Resources>
    <Resource Language="pt-BR" />
  </Resources>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop"
                        MinVersion="10.0.17763.0"
                        MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <Capability Name="internetClient" />
    <Capability Name="privateNetworkClientServer" />
  </Capabilities>

  <Applications>
    <Application Id="AvisadorCF2A" Executable="$exeName" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="Avisador CF2A"
        Description="Aplicativo de consulta CF2A"
        BackgroundColor="#2a5298"
        Square150x150Logo="assets\logo150.png"
        Square44x44Logo="assets\logo44.png">
        <uap:DefaultTile
          ShortName="Avisador CF2A"
          Wide310x150Logo="assets\logowide.png"
          Square310x310Logo="assets\logo310.png" />
        <uap:SplashScreen Image="assets\splash.png" BackgroundColor="#2a5298" />
      </uap:VisualElements>
    </Application>
  </Applications>

</Package>
"@
}

function Build-Exe([string]$platform, [string]$outPath) {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $csc = Join-Path $fw 'csc.exe'
    $webview = Join-Path $src 'packages\webview2\lib\net462'

    $args = @(
        '/noconfig', '/nostdlib+', '/target:winexe', "/platform:$platform",
        '/optimize+', '/warn:4', "/out:$outPath",
        "/r:$(Join-Path $fw 'mscorlib.dll')",
        "/r:$(Join-Path $fw 'System.dll')",
        "/r:$(Join-Path $fw 'System.Core.dll')",
        "/r:$(Join-Path $fw 'System.Drawing.dll')",
        "/r:$(Join-Path $fw 'System.Windows.Forms.dll')",
        "/r:$(Join-Path $fw 'System.Xml.dll')",
        "/r:$(Join-Path $webview 'Microsoft.Web.WebView2.Core.dll')",
        "/r:$(Join-Path $webview 'Microsoft.Web.WebView2.WinForms.dll')",
        "/win32manifest:$(Join-Path $src 'app.manifest')",
        "/win32icon:$(Join-Path $src 'app.ico')",
        (Join-Path $src 'Program.cs')
    )

    & $csc @args
    if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao ($platform)." }
}

function New-Payload([string]$arch, [string]$payloadDir) {
    if (Test-Path $payloadDir) { Remove-Item -Recurse -Force $payloadDir }
    $htmlDir = Join-Path $payloadDir 'html'
    $assetDir = Join-Path $payloadDir 'assets'
    New-Item -ItemType Directory -Force -Path $htmlDir, $assetDir | Out-Null

    Copy-Item (Join-Path $build "$arch\$exeName") $payloadDir
    Copy-Item (Join-Path $src 'packages\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll') $payloadDir
    Copy-Item (Join-Path $src 'packages\webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll') $payloadDir
    Copy-Item (Join-Path $src "packages\webview2\build\native\$arch\WebView2Loader.dll") $payloadDir
    Copy-Item $htmlSource $htmlDir
    Copy-Item (Join-Path $src 'assets\*.png') $assetDir

    $manifestPath = Join-Path $payloadDir 'AppxManifest.xml'
    [System.IO.File]::WriteAllText($manifestPath, (Get-Manifest $arch), (New-Object System.Text.UTF8Encoding $false))
}

$arches = if ($Arch -eq 'all') { @('x64', 'x86') } else { @($Arch) }

if (-not $SkipAssets -or -not (Test-Path (Join-Path $src 'app.ico'))) {
    & (Join-Path $src 'make-assets.ps1')
}

if (-not (Test-Path $htmlSource)) { throw "HTML nao encontrado: $htmlSource" }

New-Item -ItemType Directory -Force -Path $build | Out-Null
$makeappx = Find-KitsTool 'makeappx.exe'
$signtool = Find-KitsTool 'signtool.exe'

$certificate = $null
if ($Sign) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
            -FriendlyName 'Avisador CF2A (teste local)'
        "Certificado de teste criado: $($certificate.Thumbprint)"
    }
    else {
        "Certificado reutilizado: $($certificate.Thumbprint)"
    }
}

$packages = @()
foreach ($arch in $arches) {
    $archDir = Join-Path $build $arch
    New-Item -ItemType Directory -Force -Path $archDir | Out-Null

    "Compilando $arch..."
    Build-Exe $arch (Join-Path $archDir $exeName)

    $payloadDir = Join-Path $archDir 'payload'
    New-Payload $arch $payloadDir

    $msix = Join-Path $build "AvisadorCF2A_${version}_$arch.msix"
    if (Test-Path $msix) { Remove-Item -Force $msix }

    & $makeappx pack /d $payloadDir /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack falhou ($arch)." }

    if ($Sign) {
        & $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint $msix
        if ($LASTEXITCODE -ne 0) { throw "signtool falhou ($arch)." }
    }

    $packages += $msix
    "Pacote gerado: $msix"
}

$bundle = $null
if ($packages.Count -gt 1) {
    $bundle = Join-Path $build "AvisadorCF2A_${version}.msixbundle"
    if (Test-Path $bundle) { Remove-Item -Force $bundle }

    $mapFile = Join-Path $build 'bundle-map.txt'
    $lines = @('[Files]')
    foreach ($pkg in $packages) {
        $lines += '"{0}"  "{1}"' -f $pkg, (Split-Path -Leaf $pkg)
    }
    [System.IO.File]::WriteAllLines($mapFile, $lines)

    & $makeappx bundle /f $mapFile /p $bundle /bv $version /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle falhou.' }
    if ($Sign) {
        & $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint $bundle
        if ($LASTEXITCODE -ne 0) { throw 'signtool falhou (bundle).' }
    }
    "Bundle gerado: $bundle"
}

""
"==== RESULTADO ===="
Get-ChildItem $build -File -Filter 'AvisadorCF2A*' | Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize | Out-String -Width 120
if ($Sign) {
    "Para instalar localmente (teste):"
    "  1) Exporte o certificado e importe em Cert:\LocalMachine\TrustedPeople (administrador)"
    "  2) Ative o Modo Desenvolvedor ou Instalacao de aplicativos alternativos"
    "  3) Add-AppxPackage -Path `"$($packages[0])`""
}
