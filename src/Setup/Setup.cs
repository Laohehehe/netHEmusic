using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;

namespace netHEmusic.Setup;

/// <summary>
/// 自签名安装包：内嵌应用 ZIP + laohehehe 证书。
/// 运行后：解压到 %LOCALAPPDATA%\Programs\netHEmusic → 安装自签名证书(无签名设备自动信任)
/// → 创建开始菜单/桌面快捷方式 → 启动应用。
/// </summary>
public static class Program
{
    public static int Main()
    {
        try
        {
            Console.WriteLine("netHEmusic 26.9.12.20 安装程序");
            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "netHEmusic");
            Directory.CreateDirectory(installDir);

            // 1) 解压应用
            using (var zip = new ZipArchive(OpenResource("app.zip"), ZipArchiveMode.Read))
            {
                zip.ExtractToDirectory(installDir, true);
            }
            Console.WriteLine("已解压应用到: " + installDir);

            // 2) 安装并信任启动证书（无签名设备自动信任；Laohehehe / LaoheTeam.top）
            byte[] cer = ReadResource("LaoheTeam.cer");
            string cerPath = Path.Combine(installDir, "LaoheTeam.cer");
            File.WriteAllBytes(cerPath, cer);
            RunTool("certutil", "-f -user -addstore Root \"" + cerPath + "\"");
            RunTool("certutil", "-f -user -addstore TrustedPublisher \"" + cerPath + "\"");
            Console.WriteLine("已安装并通过 certutil 信任 LaoheTeam.top 证书");

            // 3) 快捷方式
            string exe = Path.Combine(installDir, "NetHEmusicCP.exe");
            CreateShortcut(exe, "netHEmusic");
            Console.WriteLine("已创建开始菜单/桌面快捷方式");

            // 4) 启动
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
            Console.WriteLine("已启动 netHEmusic。");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("安装失败: " + e);
            return 1;
        }
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
        var start = new ProcessStartInfo(tool, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        try
        {
            using var p = Process.Start(start);
            if (p != null) { p.WaitForExit(15000); }
        }
        catch { /* 无 certutil 时忽略，签名信任为可选 */ }
    }

    private static void CreateShortcut(string target, string name)
    {
        try
        {
            var start = new ProcessStartInfo("powershell",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                "$ws=New-Object -ComObject WScript.Shell;$s=$ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) '" + name + ".lnk'));$s.TargetPath='" + target + "';$s.Save();" +
                "$sd=$ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) '" + name + "[netHEmusic].lnk'));$sd.TargetPath='" + target + "';$sd.Save;\"")
            { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(start);
        }
        catch { }
    }
}
