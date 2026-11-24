# Builds the installers for Ivo and Ivo Portal, each from a self-contained publish (no .NET needed on the target PC)
# into dist\app:
#   Inno Setup (installer\Ivo.iss)       dist\Ivo\IvoSetup-<version>.exe     dist\Ivo Portal\IvoPortalSetup-<version>.exe
#   WiX MSI (installer\Ivo.wxs)          dist\Ivo\IvoSetup-<version>.msi     dist\Ivo Portal\IvoPortalSetup-<version>.msi
#   Ivo's own (IvoInstall, IvoLauncher)  dist\Ivo\IvoInstall-<version>.exe   dist\Ivo Portal\IvoPortalInstall-<version>.exe
# The same sources build both: App=Portal (Inno /D, WiX -d, MSBuild -p) picks Ivo Portal's name, IDs, icon and words.
#   installer\build.ps1 [-App Ivo|Portal|All]   (All by default)
# Needs Inno Setup 6 (winget install JRSoftware.InnoSetup), WiX 5 (dotnet tool install --global wix --version 5.0.2)
# and Visual Studio's C++ linker (for the native launcher).
param([ValidateSet('Ivo', 'Portal', 'All')] [string]$App = 'All')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw 'WiX not found. Install it with: dotnet tool install --global wix --version 5.0.2' }

# the checks (encryption, secret masking, schedules, and the Portal code both apps share) must pass before anything is packaged
dotnet run --project tests\Ivo.Checks -c Release
if ($LASTEXITCODE) { throw 'Ivo checks failed: nothing was packaged' }

# Signing with the "CN=Ivo" cert from installer\make-cert.ps1, if present: UAC then shows the app's name + a publisher, not the file path.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq 'CN=Ivo' | Select-Object -First 1
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Select-Object -Last 1
# Timestamp servers, in order: one being down mustn't fail the build. The first that answers now is used (also by Inno),
# and signing a file tries the others if it still can't reach it.
$tsaList = 'http://timestamp.digicert.com', 'http://timestamp.sectigo.com', 'http://timestamp.acs.microsoft.com'
$tsa = $tsaList | Where-Object { try { Invoke-WebRequest $_ -UseBasicParsing -TimeoutSec 8 -Method Head | Out-Null; $true } catch { [bool]$_.Exception.Response } } | Select-Object -First 1
if (-not $tsa) { $tsa = $tsaList[0] }
function Sign($file, $name) {
    if (-not ($cert -and $signtool)) { return }
    foreach ($server in @($tsa) + ($tsaList | Where-Object { $_ -ne $tsa })) {
        & $signtool sign /sha1 $cert.Thumbprint /fd sha256 /d $name /tr $server /td sha256 $file
        if (-not $LASTEXITCODE) { return }
    }
    throw "Signing $file failed"
}
if (-not $cert) { Write-Warning 'No Ivo certificate (run installer\make-cert.ps1): building unsigned.' }
# native code needs Visual Studio's C++ linker; its finder (vswhere) lives in the Visual Studio Installer folder
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

function Build($app) {
    $p = @{
        Ivo    = @{ Project = 'Ivo.csproj'; Name = 'Ivo'; Setup = 'IvoSetup'; Install = 'IvoInstall' }
        Portal = @{ Project = 'Portal\Portal.csproj'; Name = 'Ivo Portal'; Setup = 'IvoPortalSetup'; Install = 'IvoPortalInstall' }
    }[$app]
    $version = ([xml](Get-Content $p.Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { $version = '1.0.0' }
    # each app's installers in a folder of their own: dist\Ivo and dist\Ivo Portal
    $out = "dist\$($p.Name)"
    New-Item -ItemType Directory -Force $out | Out-Null

    if (Test-Path dist\app) { Remove-Item dist\app -Recurse -Force }
    dotnet publish $p.Project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o dist\app
    if ($LASTEXITCODE) { throw "dotnet publish of $($p.Name) failed" }
    Sign "dist\app\$($p.Name).exe" $p.Name

    $innoArgs = @("/DAppVersion=$version", "/DApp=$app", "/O$out")
    if ($cert -and $signtool) {
        $env:PATH += ";" + (Split-Path $signtool.FullName)
        $innoSignTool = 'IvoSign=signtool.exe sign /sha1 ' + $cert.Thumbprint + ' /fd sha256 /tr ' + $tsa + ' /td sha256 /d Ivo $f'
        $innoArgs += "/S$innoSignTool", "/DSigned"
    }
    & $iscc @innoArgs installer\Ivo.iss
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    wix build -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -d "Version=$version" -d "App=$app" -loc installer\Ivo.wxl -culture en-US -o "$out\$($p.Setup)-$version.msi" installer\Ivo.wxs
    if ($LASTEXITCODE) { throw 'WiX failed' }
    Remove-Item "$out\$($p.Setup)-$version.wixpdb" -ErrorAction SilentlyContinue
    Sign "$out\$($p.Setup)-$version.exe" $p.Name
    Sign "$out\$($p.Setup)-$version.msi" $p.Name

    # Ivo's own installer (its own window, no Inno or MSI), in two parts:
    #   the window (installer\IvoInstall) is published next to the app, sharing the app's .NET runtime;
    #   that folder becomes one Brotli archive inside the launcher (installer\IvoLauncher), native code that opens at once,
    #   unpacks it with a progress card, and starts the window.
    dotnet publish installer\IvoInstall\IvoInstall.csproj -c Release "-p:Version=$version" "-p:App=$app" -o dist\setup-window
    if ($LASTEXITCODE) { throw 'Building the installer window failed' }
    if (Test-Path dist\setup-stage) { Remove-Item dist\setup-stage -Recurse -Force }
    Copy-Item dist\app dist\setup-stage -Recurse
    $windowRoot = (Resolve-Path dist\setup-window).Path
    $clash = @()
    foreach ($f in Get-ChildItem dist\setup-window -Recurse -File) {
        $rel = $f.FullName.Substring($windowRoot.Length + 1)
        $to = Join-Path dist\setup-stage $rel
        if (Test-Path $to) {
            # the same runtime file twice: it must be identical, or the window and the app would need different runtimes
            if ((Get-FileHash $to).Hash -ne (Get-FileHash $f.FullName).Hash) { $clash += $rel }
            continue
        }
        New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
        Copy-Item $f.FullName $to
    }
    # Ivo Portal is trimmed, and trimming re-saves the runtime files it passes through, so a few no longer match the
    # window's untrimmed copies byte for byte: the window runs on Portal's own (the app's copy is the one kept). Ivo isn't
    # trimmed, so for it any difference means the two were built against different runtimes.
    if ($clash -and $app -eq 'Portal') { Write-Warning "Using $($p.Name)'s trimmed runtime files for the installer window: $($clash -join ', ')" }
    elseif ($clash) { throw "The installer window and $($p.Name) were built with different runtime files: $($clash -join ', ')" }
    # packed by the launcher's own code, run through dotnet (its .exe asks for admin rights, which a build shouldn't need)
    dotnet build installer\IvoLauncher\IvoLauncher.csproj -c Release -o dist\setup-packer
    if ($LASTEXITCODE) { throw 'Building the packer failed' }
    dotnet dist\setup-packer\IvoInstall.dll --pack "$PWD\dist\setup-stage" "$PWD\dist\ivo.tar.br"
    if ($LASTEXITCODE) { throw 'Packing the installer archive failed' }
    dotnet publish installer\IvoLauncher\IvoLauncher.csproj -c Release "-p:Version=$version" "-p:App=$app" "-p:Archive=$PWD\dist\ivo.tar.br" -o dist\setup-launcher
    if ($LASTEXITCODE) { throw 'Building the installer launcher failed' }
    Move-Item dist\setup-launcher\IvoInstall.exe "$out\$($p.Install)-$version.exe" -Force
    Remove-Item dist\setup-window, dist\setup-stage, dist\setup-launcher, dist\setup-packer, dist\ivo.tar.br -Recurse -Force
    Sign "$out\$($p.Install)-$version.exe" $p.Name

    Get-Item "$out\$($p.Setup)-$version.exe", "$out\$($p.Setup)-$version.msi", "$out\$($p.Install)-$version.exe" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}

foreach ($a in $(if ($App -eq 'All') { 'Ivo', 'Portal' } else { $App })) { Build $a }
