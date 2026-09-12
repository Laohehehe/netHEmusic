using System;
using Windows.Graphics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Update;

namespace netHEmusic.Windows;

public enum UpdateChoice { Update, None }

public sealed partial class UpdateWindow : Window
{
    private readonly TaskCompletionSource<UpdateChoice> _tcs = new();
    private readonly UpdateCheckResult _check;

    public UpdateWindow(UpdateCheckResult check)
    {
        InitializeComponent();
        Title = "更新";
        AppServices.Theme.Apply((FrameworkElement)Content);
        _check = check;
        VerText.Text = $"当前版本 v{check.Current}  →  最新版本 v{check.Latest}";
        BodyText.Text = string.IsNullOrEmpty(check.Body) ? "暂无更新说明。" : check.Body;
        try { AppWindow.Resize(new SizeInt32(640, 520)); Center(); } catch { }
    }

    private void Center()
    {
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(wa.X + (wa.Width - 640) / 2, wa.Y + (wa.Height - 520) / 2));
        }
        catch { }
    }

    public Task<UpdateChoice> WaitAsync() => _tcs.Task;

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(UpdateChoice.Update);
    }
    private void OnExit(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(UpdateChoice.None);
        Close();
    }

    /// <summary>轮询下载进度直到完成/失败。返回是否成功。</summary>
    public async Task<bool> RunDownloadAsync()
    {
        UpdateBtn.IsEnabled = false; ExitBtn.IsEnabled = false;
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(1000);
            var st = AppServices.Updater.State;
            StatusText.Text = "下载中 " + (int)(st.Progress * 100) + "%";
            UpdateProgress.Value = st.Progress * 100;
            if (st.Status == "done") { StatusText.Text = "下载完成，启动安装..."; UpdateProgress.Value = 100; return true; }
            if (st.Status == "error") { StatusText.Text = "下载失败: " + st.Error; return false; }
        }
        StatusText.Text = "下载超时";
        return false;
    }
}
