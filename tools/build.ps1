# build.ps1 : compile the WinUI3 self-contained (unpackaged) app
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dotnet = "E:\Devtools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { & (Join-Path $PSScriptRoot "bootstrap.ps1") }
$env:PATH = "E:\Devtools\dotnet" + ";" + $env:PATH
$csproj = Join-Path $root "src\NetHEmusicCP\NetHEmusicCP.csproj"
Write-Host "=== restore ==="
& $dotnet restore $csproj -p:Platform=x64
Write-Host "=== build ==="
& $dotnet build $csproj -c $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) {
    $hasMsix = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" -Recurse -Include "Microsoft.Build.AppxPackage.dll","Microsoft.Build.Packaging.Pri.Tasks.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hasMsix) {
        Write-Host ""
        Write-Host "==> MSIX/PRI build tasks (Microsoft.Build.AppxPackage.dll / Pri.Tasks) not found."
        Write-Host "    WinUI3 needs them to produce resources.pri. Run as admin:"
        Write-Host "      powershell -ExecutionPolicy Bypass -File tools\install-msix-tooling.ps1"
        Write-Host "    then re-run build.ps1."
    }
    throw "build failed"
}
Write-Host "output:" (Join-Path $root "src\NetHEmusicCP\bin\x64\$Configuration\net8.0-windows10.0.19041.0")
Write-Host "BUILD OK"
