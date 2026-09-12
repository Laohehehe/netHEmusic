using System;
using System.Runtime.InteropServices;

namespace netHEmusic;

/// <summary>原生控制台工具（-debugger 模式分配控制台窗口显示日志）。</summary>
public static class ConsoleTools
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsoleNative();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static bool AllocConsole()
    {
        try
        {
            if (AllocConsoleNative())
            {
                var h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, 3 /* SW_MAXIMIZE */);
                return true;
            }
        }
        catch { }
        return false;
    }
}
