# update.ps1 —— 自更新检测与打包（供运维/CI 使用；运行时更新由应用内 UpdateManager 完成）
param([string]$Repo = "Laohehehe/NET163download")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Write-Host "检查 $Repo 最新 Release ..."
try {
    $r = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent"="netHEmusic" }
    Write-Host ("最新: " + $r.tag_name)
    foreach ($a in $r.assets) { if ($a.name -like "netHEmusic_Setup*") { Write-Host ("资产: " + $a.name) } }
} catch { Write-Host ("获取失败: " + $_.Exception.Message) }

# 打补丁包：把当前构建产物压缩为将发布的资产名
$bin = Join-Path $root "src\NetHEmusicCP\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
if (Test-Path $bin) {
    $dist = Join-Path $root "dist"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "netHEmusic_Setup_26.9.12.22.zip"
    Write-Host ("打包: " + (Join-Path $bin "NetHEmusicCP.exe"))
    # 生成发布 zip（失败不中断，仅提示）
    try { Compress-Archive -Path (Join-Path $bin "*") -DestinationPath $zip -Force -ErrorAction Stop; Write-Host ("已打包: " + $zip) }
    catch { Write-Host ("打包失败(可忽略): " + $_.Exception.Message) }
    Write-Host ("已打包: " + $zip)
}
