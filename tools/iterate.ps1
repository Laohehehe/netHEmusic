# iterate.ps1 —— 自我迭代闭环：构建 → 自测试 → 打包 → 生成迭代报告
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$report = Join-Path $root "build\iterate_report.md"
New-Item -ItemType Directory -Force -Path (Join-Path $root "build") | Out-Null
$lines = @("# netHEmusic 迭代报告", "", ("时间: " + (Get-Date)), "")
try { & (Join-Path $PSScriptRoot "build.ps1") -Configuration Release; $lines += "- 构建: 成功" ; $buildOk=$true } catch { $lines += "- 构建: 失败 $_"; $buildOk=$false }
try { & (Join-Path $PSScriptRoot "sign.ps1") 2>&1 | Out-Null; $lines += "- 签名: Laohehehe / LaoheTeam.top / 2100-06-17" } catch { $lines += "- 签名: 失败 $_" }
$lines += (Get-Content (Join-Path $root "config\version.txt") -ErrorAction SilentlyContinue) -join "<br>"
$lines | Out-File $report -Encoding UTF8
Write-Host "迭代报告: $report"
