using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Windows;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace netHEmusic.Windows;

/// <summary>WinUI3 设置窗口：设置调整映射到 HTML 前端（主题/配色/下载/代理/语言/日志/桌面歌词/关闭到托盘），全部持久化到 config。</summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Title = "设置";
        AppServices.Theme.Apply((FrameworkElement)Content);
        try { AppWindow.Resize(new SizeInt32(760, 640)); Center(); } catch { }
        Load();
    }
    private void Center()
    {
        try { var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea; AppWindow.Move(new PointInt32(wa.X + (wa.Width - 760) / 2, wa.Y + (wa.Height - 640) / 2)); } catch { }
    }

    private void Load()
    {
        VerLabel.Text = "版本 v" + AppServices.Version;
        foreach (var name in AppServices.Theme.SchemeNames()) SchemeBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        var idx = AppServices.Theme.SchemeNames().ToList().IndexOf(AppServices.Config.Scheme); SchemeBox.SelectedIndex = idx < 0 ? 0 : idx;
        MicaToggle.IsOn = AppServices.Config.Mica;
        CloseTrayToggle.IsOn = AppServices.Config.Get("App", "close_to_tray", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        DlDirInput.Text = AppServices.Config.DownloadDir;
        QualityBox.SelectedIndex = AppServices.Config.Quality switch { "standard" => 0, "lossless" => 2, _ => 1 };
        ProxyInput.Text = AppServices.Config.Proxy;
        LangBox.SelectedIndex = AppServices.Config.Language == "en_US" ? 1 : 0;
        DeskLyricToggle.IsOn = AppServices.Config.Get("App", "desktop_lyric", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        DeskLyricTopToggle.IsOn = AppServices.Config.DesktopLyricTopmost;
        DeskSongInfoToggle.IsOn = AppServices.Config.DesktopSongInfo;
        VolumeSlider.Value = AppServices.Config.Volume;
        var pm = AppServices.Config.PlayMode;
        PlayModeBox.SelectedIndex = pm switch { "list" => 1, "single" => 2, "random" => 3, _ => 0 };
        ToastToggle.IsOn = AppServices.Config.Get("App", "toast", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        AboutVer.Text = "netHEmusic 版本 v" + AppServices.Version;
        AboutDesc.Text = "第三方网易云音乐下载/播放器 · WinUI3 + Fluent · MIT 许可证 · 与网易云音乐官方无关，仅供学习交流。" + Environment.NewLine + "构建于 " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    }
    private void OnVolume(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (sender is Slider sl) AppServices.Player.SetVolume((int)sl.Value); }
    private void OnPlayMode(object sender, SelectionChangedEventArgs e) { AppServices.Config.PlayMode = PlayModeBox.SelectedIndex switch { 1 => "list", 2 => "single", 3 => "random", _ => "order" }; }
    private void OnToast(object sender, RoutedEventArgs e) { AppServices.Config.Set("App", "toast", ToastToggle.IsOn ? "true" : "false"); }

    private void ApplyTheme() { AppServices.OnThemeApplied?.Invoke(); }

    private void OnScheme(object sender, SelectionChangedEventArgs e) { if (SchemeBox.SelectedItem is ComboBoxItem c && c.Tag is string s) { AppServices.Config.Scheme = s; ApplyTheme(); } }
    private void OnMica(object sender, RoutedEventArgs e) { AppServices.Config.Mica = MicaToggle.IsOn; }
    private void OnCloseTray(object sender, RoutedEventArgs e) { AppServices.Config.Set("App", "close_to_tray", CloseTrayToggle.IsOn ? "true" : "false"); }
    private void OnQuality(object sender, SelectionChangedEventArgs e) { AppServices.Config.Quality = QualityBox.SelectedIndex switch { 0 => "standard", 2 => "lossless", _ => "high" }; }
    private void OnLang(object sender, SelectionChangedEventArgs e) { var code = LangBox.SelectedIndex == 1 ? "en_US" : "zh_cn"; AppServices.Lang.SetLanguage(code); AppServices.Config.Language = code; }
    private void OnOpenLog(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppServices.Config.DataDir, "logs")) { UseShellExecute = true }); } catch { } }
    private void OnClearLog(object sender, RoutedEventArgs e) { try { File.WriteAllText(Path.Combine(AppServices.Config.DataDir, "logs", "log.txt"), ""); } catch { } }
    private void OnDeskLyric(object sender, RoutedEventArgs e) { AppServices.Config.Set("App", "desktop_lyric", DeskLyricToggle.IsOn ? "true" : "false"); }
    private void OnDeskLyricTop(object sender, RoutedEventArgs e) { AppServices.Config.DesktopLyricTopmost = DeskLyricTopToggle.IsOn; }
    private void OnDeskSongInfo(object sender, RoutedEventArgs e) { AppServices.Config.DesktopSongInfo = DeskSongInfoToggle.IsOn; }

    private void OnBrowseDir(object sender, RoutedEventArgs e)
    {
        try { var picker = new FolderPicker(); var hw = WinRT.Interop.WindowNative.GetWindowHandle(this); WinRT.Interop.InitializeWithWindow.Initialize(picker, hw); picker.FileTypeFilter.Add("*"); _ = Task.Run(async () => { var f = await picker.PickSingleFolderAsync(); if (f != null) AppServices.RunOnUi(() => DlDirInput.Text = f.Path); }); } catch { }
    }
    private void OnSaveDir(object sender, RoutedEventArgs e) { AppServices.Config.DownloadDir = DlDirInput.Text; }
    private void OnSaveProxy(object sender, RoutedEventArgs e) { AppServices.Config.Set("Network", "proxy", ProxyInput.Text ?? ""); }

    private void OnDone(object sender, RoutedEventArgs e) { Close(); }
}
