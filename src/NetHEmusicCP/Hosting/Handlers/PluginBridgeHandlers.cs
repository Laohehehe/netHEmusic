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
    // ================= 插件 =================

    private void HandlePluginsList()
        => PostToWeb(new { type = "plugins", list = AppServices.Plugins.List(), perms = netHEmusic.Core.Plugins.PluginService.AllPermissions, dir = AppServices.Plugins.PluginsDir, market = AppServices.Plugins.MarketUrl });

    /// <summary>把某个插件的代码交给网页端执行（只有启用的插件才会被下发）。</summary>
    private void HandlePluginCode(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        if (!AppServices.Plugins.IsEnabled(id)) { PostToWeb(new { type = "plugin_code", id, ok = false, error = "插件未启用" }); return; }
        var code = AppServices.Plugins.ReadCode(id, out var err);
        if (code is null) { PostToWeb(new { type = "plugin_code", id, ok = false, error = err }); return; }
        PostToWeb(new { type = "plugin_code", id, ok = true, code });
    }

    private void HandlePluginToggle(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        bool on = doc.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
        if (!netHEmusic.Core.Plugins.PluginService.SafeId(id)) return;
        AppServices.Plugins.SetEnabled(id, on);
        HandlePluginsList();
    }

    /// <summary>网页重载 / 手动点「重新加载」时，把所有启用插件的代码重新下发。</summary>
    private void HandlePluginReload()
    {
        foreach (var item in AppServices.Plugins.List())
        {
            var id = item.GetType().GetProperty("id")?.GetValue(item)?.ToString() ?? "";
            var enabled = item.GetType().GetProperty("enabled")?.GetValue(item) as bool? ?? false;
            if (!enabled) continue;
            var code = AppServices.Plugins.ReadCode(id, out var err);
            if (code is null) { PostToWeb(new { type = "plugin_code", id, ok = false, error = err }); continue; }
            PostToWeb(new { type = "plugin_code", id, ok = true, code });
        }
    }

    private void OpenPluginsDir()
    {
        try
        {
            var dir = AppServices.Plugins.PluginsDir;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            LogManager.Log("已打开插件目录: " + dir);
        }
        catch (Exception ex) { LogManager.Error("打开插件目录失败: " + ex.Message); }
    }

    private void HandlePluginData(JsonElement doc, bool write)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        var name = doc.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        if (!AppServices.Plugins.IsEnabled(id)) { PostToWeb(new { type = "plugin_data", id, name, ok = false, error = "插件未启用" }); return; }
        if (write)
        {
            var content = doc.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
            var ok = AppServices.Plugins.WriteData(id, name, content, out var err);
            PostToWeb(new { type = "plugin_data", id, name, ok, error = err });
        }
        else
        {
            var content = AppServices.Plugins.ReadData(id, name, out var err);
            PostToWeb(new { type = "plugin_data", id, name, ok = err.Length == 0, content, error = err });
        }
    }

    private async Task HandlePluginHttpAsync(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        var rid = doc.TryGetProperty("rid", out var rr) ? (rr.GetString() ?? "") : "";
        if (!AppServices.Plugins.IsEnabled(id)) { PostToWeb(new { type = "plugin_http", id, rid, ok = false, error = "插件未启用" }); return; }
        var method = doc.TryGetProperty("method", out var m) ? (m.GetString() ?? "GET") : "GET";
        var url = doc.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
        var body = doc.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        var (ok, text, err) = await AppServices.Plugins.HttpAsync(method, url, body);
        PostToWeb(new { type = "plugin_http", id, rid, ok, body = text, error = err });
    }

    private async Task HandlePluginMarketAsync()
    {
        var (ok, json, err) = await AppServices.Plugins.MarketAsync();
        PostToWeb(new { type = "plugin_market", ok, json, error = err });
    }

    private async Task HandlePluginInstallAsync(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        var url = doc.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
        var (ok, err) = await AppServices.Plugins.InstallAsync(id, url);
        PostToWeb(new { type = "plugin_install", id, ok, error = err });
        if (ok) HandlePluginsList();
    }

    private void HandlePluginUninstall(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "";
        var (ok, err) = AppServices.Plugins.Uninstall(id);
        PostToWeb(new { type = "plugin_uninstall", id, ok, error = err });
        HandlePluginsList();
    }
}