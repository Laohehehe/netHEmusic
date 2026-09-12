# bootstrap.ps1 —— 自设置：安装 .NET SDK（若缺失），优先使用加速镜像
param([string]$Channel = "10.0", [string]$AzureFeed = "https://mirrors.huaweicloud.com/dotnet/")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tool = "E:\Devtools"
$dotnet = "E:\Devtools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    New-Item -ItemType Directory -Force -Path $tool | Out-Null
    $script = Join-Path $tool "dotnet-install.ps1"
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/dotnet/install-scripts/main/src/dotnet-install.ps1" -OutFile $script -UseBasicParsing
    & $script -Channel $Channel -InstallDir (Join-Path $tool "dotnet") -NoPath -AzureFeed $AzureFeed
}
& $dotnet --version
Write-Host "bootstrap OK: $dotnet"
