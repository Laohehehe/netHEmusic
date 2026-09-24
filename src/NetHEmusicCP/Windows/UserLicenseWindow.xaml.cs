using System;
using Windows.Graphics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using netHEmusic.Core;
using netHEmusic.Core.Logging;

namespace netHEmusic.Windows;

public sealed partial class UserLicenseWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly string _password;

    public UserLicenseWindow()
    {
        InitializeComponent();
        Title = "用户协议";
        AppServices.Theme.Apply((FrameworkElement)Content);
        _password = new Random().Next(100000, 999999).ToString();
        LicenseText.Text = BuildAgreement();
        PasswordLine.Text = "软件启用密码 : " + _password;
        // 居中
        try { AppWindow.Resize(new SizeInt32(920, 640)); } catch { }
    }

    private void RootCenter()
    {
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(wa.X + (wa.Width - 920) / 2, wa.Y + (wa.Height - 640) / 2));
        }
        catch { }
    }

    public Task<bool> WaitAsync() => _tcs.Task;

    private void OnAgree(object sender, RoutedEventArgs e)
    {
        if ((PassInput.Text ?? "").Trim() == _password)
        {
            AppServices.Config.FirstRun = false;
            LogManager.Log("用户协议已同意");
            _tcs.TrySetResult(true);
            Close();
        }
        else
        {
            LogManager.Warn("用户协议密码错误");
            _tcs.TrySetResult(false);
            Close();
        }
    }

    private void OnDisagree(object sender, RoutedEventArgs e)
    {
        LogManager.Log("用户协议不同意，退出");
        _tcs.TrySetResult(false);
        Close();
    }

    private static string BuildAgreement() =>
@"欢迎使用 netHEmusic（网易云音乐下载器CP版）。在使用本软件前，请仔细阅读并理解本用户协议。

一、性质声明
本软件为第三方独立开发软件，与网易云音乐官方软件无任何关系，未经网易云音乐官方授权或认可。本软件不归属、不代理、不代表网易云音乐官方。

二、使用许可
本软件采用 MIT 许可证发布（详见仓库根目录 LICENSE）。你可以自由使用、修改、分发本软件，包括用于商业用途，只需在使用或分发时保留版权声明与许可声明。

三、使用建议
本软件与网易云音乐官方无关，请勿将其用于任何侵犯网易云音乐官方及其用户权益的行为，也不要用于任何违法用途。请将本软件用于个人学习、技术交流与研究。

四、服务与责任
本软件通过公开接口获取音乐信息，相关歌曲资源版权归原权利人所有。用户下载的音乐仅供个人学习研究，请于 24 小时内删除。因使用本软件产生的一切后果由用户自行承担，开发者不承担任何法律责任。

五、数据与隐私
本软件仅在本机保存配置与必要的登录凭据，并采用系统级加密（DPAPI）保护。开发者不收集、不上传、不出售任何用户个人数据。

六、协议变更
开发者有权在不通知的情况下更新本协议，更新后以最新版本为准。继续使用视为接受最新协议。

请于下方输入协议结尾显示的 6 位数字启用密码，以确认您已阅读、理解并同意上述全部条款。点击【同意并继续】即表示您接受本协议；点击【不同意并退出】将关闭程序。";
}
