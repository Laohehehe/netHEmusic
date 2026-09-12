# sign.ps1 : self-sign (Laohehehe / issuer LaoheTeam.top / expires 2100-06-17) + trust + verify
param([string]$ExePath = "", [string]$PfxPass = "Laohehehe")
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $root "src\NetHEmusicCP\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\NetHEmusicCP.exe" }
if (-not (Test-Path $ExePath)) { throw "exe not found: $ExePath" }

# signtool (NuGet BuildTools first, then Windows Kits)
$bt = "E:\Devtools\nuget-packages\microsoft.windows.sdk.buildtools\10.0.22621.3233\bin\10.0.22621.0\x64\signtool.exe"
$signtool = $null
if (Test-Path $bt) { $signtool = $bt }
else { $signtool = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1).FullName }
if (-not $signtool) { throw "signtool not found" }

# clean old certs then create CA (issuer) + leaf (signer)
Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.Subject -match "LaoheTeam|Laohehehe" } | Remove-Item -Force -ErrorAction SilentlyContinue
$ca = New-SelfSignedCertificate -Type Custom -Subject "CN=LaoheTeam.top" -FriendlyName "LaoheTeam CA" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date "2100-06-17") -KeyUsage CertSign,CRLSign,DigitalSignature -TextExtension @("2.5.29.19={critical}{text}ca=1","2.5.29.37={text}1.3.6.1.5.5.7.3.3")
$leaf = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Laohehehe" -FriendlyName "netHEmusic signer" -Signer $ca -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date "2100-06-17") -KeyUsage DigitalSignature -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

New-Item -ItemType Directory -Force -Path (Join-Path $root "build") | Out-Null
$pfx = Join-Path $root "resources\Laohehehe.pfx"
$cer = Join-Path $root "resources\LaoheTeam.cer"
$pass = ConvertTo-SecureString $PfxPass -Force -AsPlainText
Export-PfxCertificate -Cert $leaf -FilePath $pfx -Password $pass | Out-Null
Export-Certificate -Cert $ca -FilePath $cer -Force | Out-Null

# trust the issuer cert
& "C:\Windows\System32\certutil.exe" -f -user -addstore Root $cer | Out-Null
& "C:\Windows\System32\certutil.exe" -f -user -addstore TrustedPublisher $cer | Out-Null
Write-Host ("cert issuer=" + $ca.Subject + " signer=" + $leaf.Subject + " expires=" + $leaf.NotAfter)

# sign + verify
& $signtool sign /fd SHA256 /f $pfx /p $PfxPass /sha1 $leaf.Thumbprint $ExePath
Write-Host ("sign exit=" + $LASTEXITCODE)
$sig = Get-AuthenticodeSignature $ExePath
Write-Host ("auth Status=" + $sig.Status + " | Subject=" + $sig.SignerCertificate.Subject + " | Issuer=" + $sig.SignerCertificate.Issuer)
Write-Host "signed: $ExePath"