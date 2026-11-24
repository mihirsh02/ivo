# One-time: creates the self-signed "Ivo" code-signing certificate and trusts it on THIS PC.
# Run in an admin PowerShell:  .\installer\make-cert.ps1
# Other PCs: copy installer\cert\Ivo.cer there and run the two Import-Certificate lines (as admin).
# Keep the private key (in your Cert:\CurrentUser\My store) to yourself: whoever has it can sign files these PCs trust.
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Run this from an admin PowerShell (right-click PowerShell > Run as administrator).'
}
$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | ? Subject -eq 'CN=Ivo' | Select -First 1
if (-not $c) {
    $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Ivo' -FriendlyName 'Ivo code signing' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
}
New-Item -ItemType Directory -Force "$PSScriptRoot\cert" | Out-Null
Export-Certificate -Cert $c -FilePath "$PSScriptRoot\cert\Ivo.cer" | Out-Null
Import-Certificate -FilePath "$PSScriptRoot\cert\Ivo.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath "$PSScriptRoot\cert\Ivo.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
"Ivo certificate ready and trusted: $($c.Thumbprint)"
