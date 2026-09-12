using System;
using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.UI.Xaml;
using netHEmusic.Core;
using netHEmusic.Core.Logging;

namespace netHEmusic.Windows;

public sealed partial class CertificateWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public CertificateWindow()
    {
        InitializeComponent();
        Title = "安全提示";
        AppServices.Theme.Apply((FrameworkElement)Content);
        try { AppWindow.Resize(new SizeInt32(620, 400)); Center(); } catch { }
    }

    private void Center()
    {
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(wa.X + (wa.Width - 620) / 2, wa.Y + (wa.Height - 400) / 2));
        }
        catch { }
    }

    public Task<bool> WaitAsync() => _tcs.Task;

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在安装证书...";
        bool ok = AppServices.InstallCertificate();
        if (ok && AppServices.CertificatePresent())
        {
            LogManager.Log("证书安装成功，继续启动");
            _tcs.TrySetResult(true);
            Close();
        }
        else
        {
            StatusText.Text = "安装失败：请以管理员身份运行或在设置中手动安装证书。";
            LogManager.Warn("证书安装失败");
            _tcs.TrySetResult(false);
            Close();
        }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        LogManager.Log("用户拒绝安装证书，退出");
        _tcs.TrySetResult(false);
        Close();
    }
}
