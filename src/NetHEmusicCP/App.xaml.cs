using Microsoft.UI.Xaml;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Playback;
using netHEmusic.Windows;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace netHEmusic;

/// <summary>
/// 应用程序入口。负责初始化服务、全局异常捕获（自修复日志）、并判断启动参数。
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static Mutex? _singleMutex;
    private static EventWaitHandle? _activateEvent;
    private static IntPtr _mainHwnd;
    private static TrayIcon? _tray;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>单实例：若已运行则把已运行实例调到前台并退出本实例。</summary>
    private static bool AcquireSingleInstance()
    {
        bool createdNew;
        _singleMutex = new Mutex(true, "netHEmusic_SingleInstance_Lock", out createdNew);
        if (!createdNew)
        {
            try { _activateEvent = EventWaitHandle.OpenExisting("netHEmusic_Activate_Event"); _activateEvent.Set(); } catch { }
            return false;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "netHEmusic_Activate_Event");
        return true;
    }

    private static void RegisterMainWindow(Window w)
    {
        _mainHwnd = Core.Native.WindowHelper.Hwnd(w);
        // 监听“新实例已启动”信号 → 把本实例调到前台
        _ = Task.Run(() => { try { while (_activateEvent != null) { _activateEvent.WaitOne(); TrayIcon.ShowMainWindow(_mainHwnd); } } catch { } });
        // 关闭=最小化到托盘（可配置）
        try
        {
            w.AppWindow.Closing += (s, e) =>
            {
                if (AppServices.Config.Get("App", "close_to_tray", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    try { ShowWindow(_mainHwnd, 0); } catch { }
                    EnsureTray();
                }
            };
        }
        catch (Exception ex) { LogManager.Debug("关闭到托盘: " + ex.Message); }
    }

    private static void EnsureTray()
    {
        if (_tray is null && _mainHwnd != IntPtr.Zero)
        {
            _tray = new TrayIcon(_mainHwnd,
                onOpen: () => TrayIcon.ShowMainWindow(_mainHwnd),
                onExit: () => ExitApp());
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    public App()
    {
        // 显式 AppUserModelID：任务栏 / Windows 媒体卡片不再显示“未知应用”
        try { SetCurrentProcessExplicitAppUserModelID("Laohehehe.netHEmusic"); } catch { }
        InitializeComponent();
        // 全局未处理异常：写日志并提示，避免静默崩溃（自修复）
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
        // 0) 单实例：若已运行则调到前台并退出
        if (!AcquireSingleInstance()) { LogManager.Log("已有实例在运行，唤醒后退出"); Environment.Exit(0); return; }

        // 1) 初始化全局服务（日志、配置、语言、主题、网络、下载、更新、播放）
        AppServices.Initialize();

        var cmd = Environment.GetCommandLineArgs();
        bool debuggerMode = cmd.Any(a => a.Equals("-debugger", StringComparison.OrdinalIgnoreCase));

        // 2) -debugger 模式：分配控制台窗口并实时显示日志
        if (debuggerMode)
        {
            ConsoleTools.AllocConsole();
            LogManager.Log("已启用 -debugger 调试模式（控制台日志窗口）");
        }
        else
        {
            LogManager.Log("netHEmusic 启动（无控制台模式）");
        }

        LogManager.Log($"版本 {AppServices.Version} | PID {Environment.ProcessId} | OS {Environment.OSVersion.Version}");

        // 3) 启动证书检测：计算机未安装启动证书则显示“安装证书”提示，安装成功后才继续，否则退出
        if (!AppServices.CertificatePresent())
        {
            LogManager.Warn("未检测到启动证书，弹出安装提示");
            var certWin = new CertificateWindow();
            certWin.Activate();
            var ok = await certWin.WaitAsync();
            if (!ok || !AppServices.CertificatePresent()) { LogManager.Log("证书未安装，退出"); ExitApp(); return; }
        }

        // 4) 用户协议：首次运行必须输入协议里的启用密码才算通过；不通过就退出，不加载主窗口
        if (AppServices.Config.FirstRun)
        {
            LogManager.Log("首次运行：弹出用户协议");
            var licenseWin = new netHEmusic.Windows.UserLicenseWindow();
            licenseWin.Activate();
            bool agreed = await licenseWin.WaitAsync();
            if (!agreed || AppServices.Config.FirstRun)
            {
                LogManager.Warn("用户协议未通过，退出（不加载主窗口）");
                ExitApp();
                return;
            }
            LogManager.Log("用户协议已通过，继续启动");
        }

        // 5) 启动 主窗口（更新检测 → 进入主界面）
        _window = new MainWindow();
        _window.Activate();

        LogManager.Log("MainWindow 已激活");
        RegisterMainWindow(_window);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "netHEmusic");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "startup_error.txt"), "ONLAUNCH:" + Environment.NewLine + ex);
            }
            catch { }
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogManager.Log("未处理异常(UI): " + e.Exception);
        AppServices.SelfRepair.Report(e.Exception);
        e.Handled = false; // 交由默认处理，避免吞掉崩溃；已记录到日志
    }

    private void OnDomainException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogManager.Log("未处理异常(域): " + (e.ExceptionObject as Exception)?.ToString());
    }

    /// <summary>退出（供其它窗口/更新应用后调用）。</summary>
    public static void ExitApp()
    {
        LogManager.Log("应用退出");
        try { _tray?.Dispose(); } catch { }
        AppServices.Updater.CloseInstallIfRunning();
        (Application.Current as App)?._window?.Close();
        Environment.Exit(0);
    }
}
