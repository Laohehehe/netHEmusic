# run.ps1 —— 自运行：构建后启动应用（可带 -debugger）
param([string]$Configuration = "Debug", [switch]$Debugger)
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration
$exe = Join-Path $root "src\NetHEmusicCP\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\NetHEmusicCP.exe"
if (-not (Test-Path $exe)) { throw "未找到 $exe" }
$args = @()
if ($Debugger) { $args += "-debugger" }
Start-Process $exe -ArgumentList $args
Write-Host ("启动: " + $exe)
