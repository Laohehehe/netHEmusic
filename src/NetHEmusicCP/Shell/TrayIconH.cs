using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using netHEmusic.Core.Logging;

namespace netHEmusic.Shell;

/// <summary>
/// 系统托盘（H.NotifyIcon.WinUI）：<see cref="TaskbarIcon"/> + WinUI MenuFlyout。
/// 取代手写的 Shell_NotifyIcon/WM_TRAYICON/TrackPopupMenu 实现，行为契约保持不变：
/// 左键恢复主窗口；右键菜单 = 打开 / 上一首 / 暂停|播放 / 下一首 / 播放模式×4 / 桌面歌词 / 退出。
/// 菜单状态（播放中、当前模式、歌词开关）每次弹出前实时刷新。
/// 旧的 Shell/TrayIcon.cs 保留未删，出问题可改 App 里的一行切回。
/// </summary>
public sealed class TrayIconH : IDisposable
{
    private const string TipText = "netHEmusic";

    private readonly TaskbarIcon _icon;
    private readonly TrayActions? _actions;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly MenuFlyout _menu;
    private readonly MenuFlyoutItem _playItem;
    private readonly ToggleMenuFlyoutItem _lyricItem;
    private readonly RadioMenuFlyoutItem[] _modeItems;

    private static readonly (string Key, string Text)[] Modes =
    {
        ("order", "顺序播放"), ("list", "列表循环"), ("single", "单曲循环"), ("random", "随机播放")
    };

    public TrayIconH(Window window, Action onOpen, Action onExit, TrayActions? actions = null)
    {
        _onOpen = onOpen;
        _onExit = onExit;
        _actions = actions;

        _menu = new MenuFlyout();

        // ★ 重要：H.NotifyIcon 在 ContextMenuMode=PopupMenu（默认）下，把 MenuFlyout 转成
        // Win32 菜单时只执行 MenuFlyoutItem.Command，**不会触发 Click 事件**（库源码
        // TaskbarIcon.ContextMenu.WinRT.PopupMenu.cs：item.Click += (_,_) => flyoutItem.Command.TryExecute(...)）。
        // 所以这里的每一项都必须用 Command 绑定动作，写成 Click += 会表现为"菜单点不动"。
        var open = new MenuFlyoutItem { Text = "打开 netHEmusic" };
        open.Command = new DelegateCommand(() => Safe(_onOpen));
        _menu.Items.Add(open);
        _menu.Items.Add(new MenuFlyoutSeparator());

        var prev = new MenuFlyoutItem { Text = "上一首" };
        prev.Command = new DelegateCommand(() => Safe(() => _actions?.Prev?.Invoke()));
        _menu.Items.Add(prev);

        _playItem = new MenuFlyoutItem { Text = "播放" };
        _playItem.Command = new DelegateCommand(() => Safe(() => _actions?.TogglePlay?.Invoke()));
        _menu.Items.Add(_playItem);

        var next = new MenuFlyoutItem { Text = "下一首" };
        next.Command = new DelegateCommand(() => Safe(() => _actions?.Next?.Invoke()));
        _menu.Items.Add(next);

        // 播放模式：旧实现是子菜单（TrackPopupMenu 的 MF_POPUP），WinUI 转 Win32 菜单时
        // 子菜单支持不稳，这里平铺成 4 个单选，点起来反而更快。
        // 同样只能用 Command；库不处理 RadioMenuFlyoutItem 的勾选（转出的 Win32 菜单无 Checked），
        // 所以这里手动维护 IsChecked 的内部一致性，显示问题另议。
        _modeItems = new RadioMenuFlyoutItem[Modes.Length];
        for (var i = 0; i < Modes.Length; i++)
        {
            var (key, text) = Modes[i];
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "tray-playmode", Tag = key };
            item.Command = new DelegateCommand(() =>
            {
                Safe(() => _actions?.SetPlayMode?.Invoke(key));
                foreach (var modeItem in _modeItems)
                {
                    modeItem.IsChecked = string.Equals(modeItem.Tag as string, key, StringComparison.OrdinalIgnoreCase);
                }
            });
            _modeItems[i] = item;
            _menu.Items.Add(item);
        }

        _lyricItem = new ToggleMenuFlyoutItem { Text = "桌面歌词" };
        _lyricItem.Command = new DelegateCommand(() =>
        {
            // 库不会替我们翻转 ToggleMenuFlyoutItem.IsChecked，这里自己算新值并更新
            var nextValue = !SafeGet(() => _actions?.IsLyricOn?.Invoke() ?? false, false);
            Safe(() => _actions?.SetLyric?.Invoke(nextValue));
            _lyricItem.IsChecked = nextValue;
        });
        _menu.Items.Add(_lyricItem);

        _menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = "退出" };
        exit.Command = new DelegateCommand(() => Safe(_onExit));
        _menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            ToolTipText = TipText,
            ContextFlyout = _menu,
            LeftClickCommand = new DelegateCommand(() => Safe(_onOpen)),
            RightClickCommand = new DelegateCommand(RefreshMenu)
        };
        try { _icon.Icon = LoadIcon(); }
        catch (Exception e) { LogManager.Debug("托盘图标加载失败: " + e.Message); }

        // 注意：PopupMenu 模式下菜单由 MenuFlyout 转成 Win32 弹窗显示，不会走 MenuFlyout.ShowAt，
        // 所以 _menu.Opening 永远不会触发。状态刷新统一放在 RightClickCommand（右键时、生成菜单前执行）。

        // TaskbarIcon 是 FrameworkElement，必须挂进可视树才会真正创建图标
        if (window.Content is Panel root) root.Children.Add(_icon);
        else LogManager.Warn("托盘：主窗口根元素不是 Panel，TaskbarIcon 未加入可视树");

        _icon.ForceCreate();
        RefreshMenu();
        LogManager.Log("托盘图标已创建（H.NotifyIcon.WinUI）");
    }

    /// <summary>菜单勾选/文字实时刷新：读不到状态就当不可用，绝不抛。</summary>
    private void RefreshMenu()
    {
        try
        {
            var playing = SafeGet(() => _actions?.IsPlaying?.Invoke() ?? false, false);
            var mode = SafeGet(() => _actions?.PlayMode?.Invoke() ?? "order", "order") ?? "order";
            var lyric = SafeGet(() => _actions?.IsLyricOn?.Invoke() ?? false, false);

            _playItem.Text = playing ? "暂停" : "播放";
            foreach (var item in _modeItems)
                item.IsChecked = string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase);
            _lyricItem.IsChecked = lyric;
        }
        catch (Exception e) { LogManager.Debug("托盘菜单刷新失败: " + e.Message); }
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        // 直接取 exe 关联图标（和任务栏/开始菜单一致）；不走 ImageSource 转换，
        // 那条路在 WinUI 下会抛 "picture must be a picture that can be used as a Icon"
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            return System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch { return null; }
    }

    private static void Safe(Action? a) { try { a?.Invoke(); } catch (Exception e) { LogManager.Debug("托盘动作异常: " + e.Message); } }

    private static T SafeGet<T>(Func<T>? f, T def) { try { return f is null ? def : f(); } catch { return def; } }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把主窗口恢复并前置（单实例唤醒 / 托盘左键共用）。</summary>
    public static void ShowMainWindow(IntPtr hwnd)
    {
        try { ShowWindow(hwnd, 5); SetForegroundWindow(hwnd); } catch { }
    }

    public void Dispose()
    {
        try { _icon.Dispose(); } catch (Exception e) { LogManager.Debug("托盘释放异常: " + e.Message); }
    }

    /// <summary>最小 ICommand：只给 TaskbarIcon.LeftClickCommand 用，避免为一个命令引 MVVM 包。</summary>
    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;
        public DelegateCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
