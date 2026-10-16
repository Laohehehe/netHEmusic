using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using netHEmusic.Core;
using netHEmusic.Shell;
using netHEmusic.Core.Api;
using netHEmusic.Core.Download;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Platform;
using netHEmusic.Core.Playback;
using netHEmusic.Core.Theme;
using netHEmusic.Core.Update;
using netHEmusic.Windows;

namespace netHEmusic;

/// <summary>MainWindow 的部分实现（拆分自 MainWindow.xaml.cs，纯搬移，行为不变）。</summary>
public sealed partial class MainWindow
{
    // ============ 启动流程：用户协议 -> 更新检测 -> 主界面 ============
    private async Task StartupFlowAsync()
    {
        try
        {
            if (!await EnsureAgreementAsync()) { App.ExitApp(); return; }
            var check = await AppServices.Updater.Check();
            if (check.HasUpdate)
            {
                var upd = new UpdateWindow(check);
                upd.Activate();
                var choice = await upd.WaitAsync();
                if (choice == UpdateChoice.Update)
                {
                    AppServices.Updater.StartDownload(check.Latest, check.DownloadUrl, check.Sha256, check.AssetName);
                    if (await upd.RunDownloadAsync()) { if (AppServices.Updater.ApplyUpdate(out _)) { App.ExitApp(); return; } }
                }
                else { App.ExitApp(); return; }
            }
        }
        catch (Exception e) { LogManager.Error("启动流程异常: " + e); }
    }
    private async Task<bool> EnsureAgreementAsync()
    {
        if (!AppServices.Config.FirstRun) return true;
        var win = new UserLicenseWindow();
        win.Activate();
        return await win.WaitAsync();
    }

    /// <summary>config 里的版本比当前版本旧 → 通知前端弹更新公告（关闭后再回写版本号）。</summary>
    private void CheckVersionNotice()
    {
        try
        {
            var seen = (AppServices.Config.VersionSeen ?? "").Trim();
            if (!IsVersionOlder(seen, AppServices.Version)) { LogManager.Debug("版本已是最新记录: " + seen); return; }
            LogManager.Log("检测到版本变化: config=" + (string.IsNullOrEmpty(seen) ? "(空)" : seen) + " → 当前 " + AppServices.Version + "，弹出更新公告");
            PostToWeb(new { type = "update_notice", from = string.IsNullOrEmpty(seen) ? "" : seen, to = AppServices.Version });
        }
        catch (Exception e) { LogManager.Debug("版本检测失败: " + e.Message); }
    }

    private static bool IsVersionOlder(string? seen, string current)
    {
        static Version Parse(string s)
            => Version.TryParse((s ?? "").Trim(), out var v) ? v : new Version(0, 0, 0, 0);
        return Parse(seen) < Parse(current);
    }
}