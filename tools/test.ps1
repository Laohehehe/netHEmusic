# test.ps1 —— 自测试：编译 + 静态校验 + 关键路径自检
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root ".toolchain\dotnet\dotnet.exe"
Write-Host "=== [1/3] 版本一致 & 结构校验 ==="
$csproj = Get-Content (Join-Path $root "src\NetHEmusicCP\NetHEmusicCP.csproj") -Raw
if ($csproj -notmatch "<Version>26.9.12.22</Version>") { throw "csproj 版本非 26.9.12.22" }
foreach ($req in @("Core\Api\NetEaseClient.cs","Core\Config\AppConfig.cs","Core\Logging\LogManager.cs","Core\Download\DownloadManager.cs","Core\Update\UpdateManager.cs","Core\Playback\PlayerService.cs","Core\Theme\ThemeManager.cs","MainWindow.xaml.cs")) {
    if (-not (Test-Path (Join-Path $root "src\NetHEmusicCP\$req"))) { throw "缺失核心文件: $req" }
    Write-Host "  ok $req"
}
foreach ($lf in @("lang\zh_cn.lang","lang\en_US.lang")) {
    if (-not (Test-Path (Join-Path $root $lf))) { throw "缺失语言包: $lf" }
    Write-Host "  ok $lf"
}
Write-Host "=== [2/3] 构建测试 (Debug) ==="
& (Join-Path $PSScriptRoot "build.ps1") -Configuration Debug
Write-Host "=== [3/3] 综合自检 ==="
$bin = Get-ChildItem (Join-Path $root "src\NetHEmusicCP\bin\x64\Debug\net8.0-windows10.0.19041.0") -Filter "NetHEmusicCP.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($bin) { Write-Host "  产物: $($bin.Name)" } else { throw "未生成可执行产物" }
Write-Host "自测试通过"
