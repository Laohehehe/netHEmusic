using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Platform;

/// <summary>
/// 应用在 Windows Shell 里的身份（AppUserModelID）。
///
/// 只调用 SetCurrentProcessExplicitAppUserModelID 是不够的：系统媒体卡片（SMTC）、任务栏分组
/// 会拿这个 AUMID 去“开始菜单快捷方式”和 HKCU\Software\Classes\AppUserModelId 里查显示名与图标，
/// 查不到就一直显示“未知应用”。所以这里把三件事做齐：进程 AUMID、注册表显示名/图标、带 AUMID 的快捷方式。
/// 全部是幂等的“缺什么补什么”，失败也不影响应用运行。
/// </summary>
public static class AppIdentity
{
    /// <summary>进程 AUMID。改了它，系统会把本应用当成另一个应用（任务栏固定项、音量合成器都会变）。</summary>
    public const string AumId = "Laohehehe.netHEmusic";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>进程级 AUMID：必须在任何窗口创建之前调用（App 构造函数里）。</summary>
    public static void SetProcessAppUserModelId()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AumId); }
        catch { }
    }

    /// <summary>注册显示名/图标，并保证存在带 AUMID 的开始菜单快捷方式；幂等，可重复调用。</summary>
    public static void EnsureRegistered()
    {
        try { RegisterDisplayName(); } catch (Exception ex) { Log("注册应用显示名失败: " + ex.Message); }
        try { EnsureStartMenuShortcut(); } catch (Exception ex) { Log("创建开始菜单快捷方式失败: " + ex.Message); }
    }

    /// <summary>注册表里登记本 AUMID 的显示名与图标（供通知 / 任务栏使用）。</summary>
    private static void RegisterDisplayName()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AumId);
        if (key is null) return;
        key.SetValue("DisplayName", AppConfig.AppName, RegistryValueKind.String);
        key.SetValue("IconUri", AppIconSource(), RegistryValueKind.String);
    }

    /// <summary>
    /// 应用图标来源：必须是 exe 自己（图标从它的资源里取）。
    /// 实测（Windows 11）：把 IconUri / 快捷方式图标指向一个独立的 .ico 文件时，Shell 解析不出应用图标，
    /// 任务栏按钮与系统媒体卡片会退化成“空白文件”图标（白色方块）；换成 exe 路径就正常。
    /// </summary>
    private static string AppIconSource() => Environment.ProcessPath ?? "";

    /// <summary>
    /// 保证系统里有一个“指向当前 exe 且带 AUMID”的开始菜单快捷方式。
    /// 只改写指向当前 exe 的快捷方式——安装版快捷方式指向别的路径时不动它，
    /// 免得开发运行把已安装版本的入口改写成本地构建产物。
    /// </summary>
    private static void EnsureStartMenuShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var userLnk = UserShortcutPath();
        foreach (var lnk in CandidateShortcuts())
        {
            if (!File.Exists(lnk)) continue;
            var (target, aumid, icon) = Shortcut.Read(lnk);
            if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(aumid, AumId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(icon, AppIconSource(), StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Debug("开始菜单快捷方式已就绪: " + lnk);
                return;
            }
            if (Shortcut.Write(lnk, exe, AumId)) return;   // 就地补上 AUMID
        }

        // 免安装运行 / 系统里没有指向本 exe 的快捷方式 → 建一个每用户的
        if (Shortcut.Write(userLnk, exe, AumId)) Log("已写入开始菜单快捷方式: " + userLnk);
    }

    private static string UserShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Start Menu\Programs", AppConfig.AppName + ".lnk");

    /// <summary>可能已存在的开始菜单快捷方式（每用户 + 所有用户，安装器两种放法都覆盖）。</summary>
    private static IEnumerable<string> CandidateShortcuts()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs", AppConfig.AppName + ".lnk");
        yield return Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs", AppConfig.AppName, AppConfig.AppName + ".lnk");
        yield return Path.Combine(programData, @"Microsoft\Windows\Start Menu\Programs", AppConfig.AppName + ".lnk");
        yield return Path.Combine(programData, @"Microsoft\Windows\Start Menu\Programs", AppConfig.AppName, AppConfig.AppName + ".lnk");
    }

    private static void Log(string message)
    {
        try { LogManager.Log(message); } catch { }
    }

    // ---------------- 快捷方式读写：IShellLink + IPropertyStore(PKEY_AppUserModel_ID) ----------------

    private const ushort VtLpwstr = 31;          // PROPVARIANT 里的宽字符串
    private const uint SlgpRawPath = 0x00000004; // GetPath：返回未展开的原始路径

    private static readonly PropertyKey PkeyAppUserModelId = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT：x64 下 24 字节（8 字节头 + 16 字节 union），这里按最大 union 留足空间。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public long UnionTail;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    private static class Shortcut
    {
        /// <summary>写入/覆盖一个快捷方式，并把 AUMID 存进它的属性存储。</summary>
        public static bool Write(string lnkPath, string exe, string aumid)
        {
            object? com = null;
            try
            {
                var dir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                com = new ShellLink();
                var link = (IShellLinkW)com;
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
                link.SetDescription(AppConfig.AppName + " 网易云音乐下载器");
                link.SetIconLocation(exe, 0);

                var store = (IPropertyStore)com;
                var key = PkeyAppUserModelId;
                var value = new PropVariant { VarType = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(aumid) };
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally { Marshal.FreeCoTaskMem(value.Pointer); }

                ((IPersistFile)com).Save(lnkPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Log("写快捷方式失败 " + lnkPath + ": " + ex.Message);
                return false;
            }
            finally { Release(com); }
        }

        /// <summary>读快捷方式的目标路径、AUMID 与图标位置（失败返回 null）。</summary>
        public static (string? Target, string? AumId, string? Icon) Read(string lnkPath)
        {
            object? com = null;
            try
            {
                com = new ShellLink();
                ((IPersistFile)com).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)com).GetPath(sb, sb.Capacity, IntPtr.Zero, SlgpRawPath);
                var icon = new StringBuilder(1024);
                ((IShellLinkW)com).GetIconLocation(icon, icon.Capacity, out _);

                var key = PkeyAppUserModelId;
                ((IPropertyStore)com).GetValue(ref key, out var value);
                string? aumid = null;
                if (value.VarType == VtLpwstr && value.Pointer != IntPtr.Zero)
                {
                    aumid = Marshal.PtrToStringUni(value.Pointer);
                    Marshal.FreeCoTaskMem(value.Pointer);   // VT_LPWSTR 就是 CoTaskMem 分配的
                }
                return (sb.ToString(), aumid, icon.ToString());
            }
            catch { return (null, null, null); }
            finally { Release(com); }
        }

        private static void Release(object? com)
        {
            if (com is null) return;
            try { Marshal.FinalReleaseComObject(com); } catch { }
        }
    }
}
