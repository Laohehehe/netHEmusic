# selfrepair.ps1 —— 自修复：清理损坏临时文件、删除残留 .part、归档崩溃日志
$ErrorActionPreference = "SilentlyContinue"
$data = Join-Path $env:APPDATA "netHEmusic"
$count = 0
if (Test-Path $data) {
    foreach ($f in Get-ChildItem $data -Recurse -Filter "*.part" -File) { Remove-Item $f.FullName -Force; $count++ }
    # 清理过期 repair_log
    $repairLog = Join-Path $data "repair_log.txt"
    if ((Test-Path $repairLog) -and ((Get-Item $repairLog).LastWriteTime -lt (Get-Date).AddDays(-14))) { Remove-Item $repairLog -Force }
    # 若 config 损坏，备份后重置
    $ini = Join-Path $data "config.ini"
    if (Test-Path $ini) {
        try { $null = [System.IO.File]::ReadAllText($ini) } catch { Copy-Item $ini "$ini.bak" -Force; Remove-Item $ini -Force; Write-Host "config.ini 损坏，已重置" }
    }
}
Write-Host "自修复完成，清理 .part: $count"
