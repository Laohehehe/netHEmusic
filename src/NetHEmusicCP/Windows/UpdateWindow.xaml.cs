using System;
using System.Text.RegularExpressions;
using Windows.Graphics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = global::Windows.UI.Color;
using WinFontStyle = global::Windows.UI.Text.FontStyle;
using WinFontWeight = global::Windows.UI.Text.FontWeight;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Update;

namespace netHEmusic.Windows;

public enum UpdateChoice { Update, None }

public sealed partial class UpdateWindow : Window
{
    // 自建 FontWeight：Microsoft.UI.Text.FontWeights 在本机运行时会 BadImageFormat 崩溃，
    // Windows.UI.Text.FontWeights 又不在当前投影里，所以直接构造结构体。
    private static readonly WinFontWeight FW_Bold = new() { Weight = 700 };
    private static readonly WinFontWeight FW_Semi = new() { Weight = 600 };

    private readonly TaskCompletionSource<UpdateChoice> _tcs = new();
    private readonly UpdateCheckResult _check;

    public UpdateWindow(UpdateCheckResult check)
    {
        InitializeComponent();
        Title = "更新";
        AppServices.Theme.Apply((FrameworkElement)Content);
        _check = check;
        VerText.Text = $"当前版本 v{check.Current}  →  最新版本 v{check.Latest}";
        RenderMarkdown(string.IsNullOrWhiteSpace(check.Body) ? "暂无更新说明。" : check.Body);
        try { AppWindow.Resize(new SizeInt32(680, 560)); Center(); } catch { }

        // 点标题栏的 × 关闭 = 选择「退出」：不允许靠关窗口绕过更新流程
        Closed += (_, __) =>
        {
            if (!_tcs.Task.IsCompleted)
            {
                LogManager.Log("更新窗口被直接关闭 → 视为退出");
                _tcs.TrySetResult(UpdateChoice.None);
            }
        };
    }

    private void Center()
    {
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(wa.X + (wa.Width - 680) / 2, wa.Y + (wa.Height - 560) / 2));
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

    // ============ Markdown 渲染（保守实现）============
    // 逐行转成 TextBlock：标题按级别放大、列表前加圆点、剥掉 ** 与 ` 等符号。
    // 整个过程包在 try/catch 里：渲染失败就退回纯文本，绝不让更新窗口崩掉主程序。
    private void RenderMarkdown(string md)
    {
        try { RenderMarkdownCore(md); }
        catch (Exception e)
        {
            LogManager.Error("更新日志渲染失败，退回纯文本: " + e.Message);
            try
            {
                BodyPanel.Children.Clear();
                BodyPanel.Children.Add(new TextBlock
                {
                    Text = md, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Foreground = Res("AppTextBrush", WinColor.FromArgb(255, 235, 235, 245))
                });
            }
            catch { }
        }
    }

    private void RenderMarkdownCore(string md)
    {
        BodyPanel.Children.Clear();
        var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        bool first = true;

        foreach (var raw in lines)
        {
            var s = raw.TrimEnd();
            var line = s.Trim();
            if (line.Length == 0) continue;

            double size = 13;
            string text = line;
            double topMargin = first ? 0 : 6;
            first = false;

            if (line.StartsWith("# ")) { size = 19; text = line[2..]; topMargin = first ? 0 : 12; }
            else if (line.StartsWith("## ")) { size = 16.5; text = line[3..]; }
            else if (line.StartsWith("### ")) { size = 14.5; text = line[4..]; }
            else if (line.StartsWith("#### ")) { size = 13.5; text = line[5..]; }
            else if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            { text = "•  " + line[2..]; }
            else if (line is "---" or "***" or "___")
            { text = new string('─', 40); size = 11; }

            text = Clean(text);

            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                Margin = new Thickness(0, topMargin, 0, 0),
                Foreground = size >= 14.5 ? Res("AppTextBrush", WinColor.FromArgb(255, 245, 245, 250))
                                          : Res("AppTextBrush", WinColor.FromArgb(255, 225, 225, 235))
            };
            if (size >= 14.5) { try { tb.FontWeight = FW_Bold; } catch { } }
            BodyPanel.Children.Add(tb);
        }
    }

    /// <summary>去掉 Markdown 行内标记，只保留可读文字。</summary>
    private static string Clean(string s)
    {
        s = s.Replace("**", "").Replace("__", "");
        s = s.Replace("`", "");
        // [文字](链接) -> 文字
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return s.TrimEnd();
    }

    private static Brush Res(string key, WinColor fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
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