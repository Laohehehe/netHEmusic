using System;
using System.IO;
using System.Runtime.InteropServices;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Native;

/// <summary>删除文件 = 移入回收站（不直接抹掉，给用户后悔的余地）。</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;      // 进回收站（关键）
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    public static bool Delete(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var full = Path.GetFullPath(path);
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = full + "\0\0",   // 双 \0 结尾的路径列表
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            var rc = SHFileOperation(ref op);
            if (rc != 0 || op.fAnyOperationsAborted)
            {
                LogManager.Warn("移入回收站失败 rc=" + rc + " : " + full);
                return false;
            }
            LogManager.Log("已移入回收站: " + full);
            return true;
        }
        catch (Exception e) { LogManager.Error("移入回收站异常: " + e.Message); return false; }
    }
}
