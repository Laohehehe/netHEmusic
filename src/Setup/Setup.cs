using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace netHEmusic.Setup;

/// <summary>
/// netHEmusic 安装程序：内嵌应用 ZIP（+ 自签名证书）。
/// 流程：关闭正在运行的实例 → 解压到 %LOCALAPPDATA%\Programs\netHEmusic
///      → 安装并信任证书 → 创建开始菜单/桌面快捷方式 → 写卸载信息 → 启动应用。
/// 卸载：开始菜单里的「卸载 netHEmusic」或系统设置-应用里卸载。
/// </summary>
public static class Program
{
    private const string AppVersion = "26.9.12.49";
    private const string AppName = "netHEmusic";

    [STAThread]
    public static int Main()
    {
        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        try
        {
            // 0) 先关掉正在运行的实例（否则 exe 被占用，解压会失败）
            KillRunning();

            Directory.CreateDirectory(installDir);

            // 1) 解压应用
            using (var zip = new ZipArchive(OpenResource("app.zip"), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录
                    string target = Path.Combine(installDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
            }

            // 2) 证书：写到安装目录并信任（无签名设备自动信任）
            try
            {
                byte[] cer = ReadResource("LaoheTeam.cer");
                string cerPath = Path.Combine(installDir, "LaoheTeam.cer");
                File.WriteAllBytes(cerPath, cer);
                string certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");   // 别依赖 PATH
                RunTool(certutil, "-f -user -addstore Root \"" + cerPath + "\"");
                RunTool(certutil, "-f -user -addstore TrustedPublisher \"" + cerPath + "\"");
            }
            catch { /* 证书信任失败不影响使用 */ }

            // 3) 快捷方式（开始菜单 + 桌面）
            string exe = Path.Combine(installDir, "NetHEmusicCP.exe");
            MakeShortcut(exe, installDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));
            MakeShortcut(exe, installDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));

            // 4) 卸载信息（系统设置的「应用」里能看到并卸载）
            WriteUninstallEntry(installDir);

            // 5) 启动
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir }); } catch { }

            Msg("netHEmusic " + AppVersion + " 安装完成！\n\n安装位置：\n" + installDir, false);
            return 0;
        }
        catch (Exception e)
        {
            Msg("安装失败：\n" + e.Message, true);
            return 1;
        }
    }

    /// <summary>结束正在运行的 netHEmusic（安装/更新前必须）。</summary>
    private static void KillRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("NetHEmusicCP"))
            {
                try { p.CloseMainWindow(); } catch { }
            }
            foreach (var p in Process.GetProcessesByName("NetHEmusicCP"))
            {
                if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } }
            }
            System.Threading.Thread.Sleep(400);
        }
        catch { }
    }

    private static void MakeShortcut(string target, string workDir, string lnkPath)
    {
        // 直接用 WScript.Shell COM 建 .lnk（不依赖 powershell/cscript，避免引号/路径把命令拆坏）
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic ws = Activator.CreateInstance(t)!;
            dynamic lnk = ws.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            lnk.IconLocation = target;
            lnk.Save();
        }
        catch (Exception e)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "netHEmusic_setup.log"),
                DateTime.Now + " 建快捷方式失败 " + lnkPath + " : " + e.Message + Environment.NewLine); } catch { }
        }
    }

    private static void WriteUninstallEntry(string installDir)
    {
        try
        {
            string uninst = Path.Combine(installDir, "uninstall.ps1");
            File.WriteAllText(uninst, UNINSTALL_SCRIPT.Replace("__DIR__", installDir));
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);
            if (key is null) return;
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", "Laohehehe");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", Path.Combine(installDir, "NetHEmusicCP.exe"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("UninstallString",
                "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + uninst + "\"");
        }
        catch { }
    }

    private const string UNINSTALL_SCRIPT = @"$dir = '__DIR__'
Get-Process NetHEmusicCP -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'netHEmusic.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'netHEmusic.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\netHEmusic' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static void Msg(string text, bool error)
    {
        // 0x40 = 信息图标, 0x10 = 错误图标
        try { MessageBox(IntPtr.Zero, text, "netHEmusic 安装程序", error ? 0x10u : 0x40u); } catch { }
    }

    private static Stream OpenResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceStream(name) ?? throw new Exception("缺少内嵌资源 " + name);
    }

    private static byte[] ReadResource(string name)
    {
        using var s = OpenResource(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void RunTool(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(15000);
        }
        catch { }
    }
}