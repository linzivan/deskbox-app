# Sign DeskBoxes.exe with a self-signed code signing cert in CurrentUser\My
# Run after build.bat. Safe to re-run.
param(
    [string]$Exe = "DeskBoxes.exe",
    [string]$SubjectLike = "*DeskBox*"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root $Exe
if (-not (Test-Path $exePath)) { Write-Output "[SIGN] exe not found: $exePath"; exit 1 }

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like $SubjectLike } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject "CN=DeskBox Local, O=DeskBox, C=CN" `
        -KeyUsage DigitalSignature `
        -FriendlyName "DeskBoxes self-signed" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(10)
    Write-Output "[SIGN] created cert $($cert.Thumbprint)"
} else {
    Write-Output "[SIGN] using cert $($cert.Thumbprint)"
}

Export-Certificate -Cert $cert -FilePath (Join-Path $root "DeskBoxes.cer") -Force | Out-Null

$sig = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert
Write-Output "[SIGN] status: $($sig.Status)"

$trusted = Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue |
           Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if ($trusted) {
    Write-Output "[SIGN] cert already trusted (CurrentUser\Root)"
} else {
    Write-Output "[SIGN] NOTE: not trusted yet -> run tools\trust-cert.bat once"
}
