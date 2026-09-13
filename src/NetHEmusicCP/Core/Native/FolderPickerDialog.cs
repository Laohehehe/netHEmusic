using System;
using System.Runtime.InteropServices;

namespace netHEmusic.Core.Native;

/// <summary>
/// 资源管理器风格的「选择文件夹」对话框。
/// WinRT 的 Windows.Storage.Pickers.FolderPicker 在未打包（unpackaged）的 WinUI3 应用里
/// PickSingleFolderAsync 会直接抛 COMException 0x80004005(E_FAIL)，所以这里改用 Win32 的
/// IFileOpenDialog + FOS_PICKFOLDERS —— 也是系统「选择文件夹」用的那个对话框。
/// </summary>
internal static class FolderPickerDialog
{
    private const uint FOS_PICKFOLDERS = 0x00000020;    // 选文件夹而不是选文件
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>弹出选择文件夹对话框，返回选中的路径；用户取消返回 null。</summary>
    public static string? Pick(IntPtr owner, string? initialDir, string title)
    {
        IFileDialog? dlg = null;
        IShellItem? item = null;
        try
        {
            dlg = (IFileDialog)new FileOpenDialogRCW();

            dlg.GetOptions(out var opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            if (!string.IsNullOrWhiteSpace(title)) { try { dlg.SetTitle(title); } catch { } }

            // 打开时定位到当前下载目录（不存在就退回系统默认位置）
            if (!string.IsNullOrWhiteSpace(initialDir))
            {
                try
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialDir!, IntPtr.Zero, ref iid, out var folder) >= 0 && folder is not null)
                    {
                        try { dlg.SetFolder(folder); } finally { Marshal.ReleaseComObject(folder); }
                    }
                }
                catch { }
            }

            int hr = dlg.Show(owner);
            if (hr == HRESULT_CANCELLED) return null;
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            dlg.GetResult(out item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            if (item is not null) { try { Marshal.ReleaseComObject(item); } catch { } }
            if (dlg is not null) { try { Marshal.ReleaseComObject(dlg); } catch { } }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem? ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    /// <summary>COM 接口方法必须按 vtable 顺序声明，未用到的方法也不能删。</summary>
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
