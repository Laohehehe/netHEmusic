# install-msix-tooling.ps1 —— 安装 VS 2022 Build Tools 的 UWP / MSIX 打包组件（需要管理员）
# 说明：WinUI3 最终生成 resources.pri 与打包需微软 MSBuild 任务程序集：
#   Microsoft.Build.AppxPackage.dll、Microsoft.Build.Packaging.Pri.Tasks.dll
# 这些由 VS Build Tools 工作负载提供。需要以管理员运行本脚本。
# 用法（在管理员 PowerShell 中）：
#   powershell -ExecutionPolicy Bypass -File tools\install-msix-tooling.ps1
$ErrorActionPreference = "Stop"
$vsWhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vsWhere)) { $vsWhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe" }

Write-Host "检查 VS Build Tools ..."
$bt = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
if (-not (Test-Path $bt)) { throw "未找到 VS 2022 Build Tools，请先安装，或用 Visual Studio Installer 添加组件。" }

# 找到 VS 安装器
$installer = @("C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe",
               "C:\Program Files\Microsoft Visual Studio\Installer\setup.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $installer) { throw "未找到 Visual Studio Installer (setup.exe)。" }

Write-Host "正在通过 VS 安装器添加 UWP/MSIX 打包组件（需管理员授权）..."
# Microsoft.VisualStudio.ComponentGroup.MSIX.Packaging -> MSIX 打包工具；Microsoft.VisualStudio.Workload.Universal -> UWP
$components = @("Microsoft.VisualStudio.ComponentGroup.MSIX.Packaging", "Microsoft.VisualStudio.Workload.Universal")
$args = @("modify", "--installPath", $bt, "--add", ($components -join " --add "), "--passive", "--norestart", "--wait")
Write-Host ("setup.exe " + ($args -join " "))
Start-Process $installer -ArgumentList $args -Wait -Verb RunAs
Write-Host "安装完成。请重新运行 tools\build.ps1。"
