using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.UI.Dispatching;
using netHEmusic.Core.Api;
using netHEmusic.Core.Cache;
using netHEmusic.Core.Config;
using netHEmusic.Core.Download;
using netHEmusic.Core.Localization;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Playback;
using netHEmusic.Core.Theme;
using netHEmusic.Core.Update;

namespace netHEmusic.Core;

/// <summary>
/// 全局服务容器：集中初始化并暴露单例服务，提供跨线程事件回 UI 线程的广播。
/// </summary>
public static class AppServices
{
    public const string Version = "26.9.24.31";   // 版本号 = 26.M.D.X，X 为当日修改次数（每次改动 +1）
    public static AppConfig Config { get; private set; } = null!;
    public static NetEaseClient Netease { get; private set; } = null!;
    public static DownloadManager Download { get; private set; } = null!;
    public static CacheManager Cache { get; private set; } = null!;
    public static UpdateManager Updater { get; private set; } = null!;
    public static PlayerService Player { get; private set; } = null!;
    public static ThemeManager Theme { get; private set; } = null!;
    public static LangService Lang { get; private set; } = null!;
    public static SelfRepair SelfRepair { get; private set; } = null!;
    public static Plugins.PluginService Plugins { get; private set; } = null!;

    /// <summary>主题应用到主界面+推送前端的回调（由 MainWindow 注册）。</summary>
    public static Action? OnThemeApplied;

    private static DispatcherQueue? _ui;
    /// <summary>主线程 Dispatcher，MainWindow 启动时注入。</summary>
    public static DispatcherQueue Ui { get => _ui!; set => _ui = value; }

    /// <summary>把后台事件安全地调度到 UI 线程。</summary>
    public static void RunOnUi(Action a)
    {
        if (_ui is not null && _ui.HasThreadAccess) a();
        else _ui?.TryEnqueue(() => { try { a(); } catch { } });
    }

    /// <summary>
    /// API 服务换过供应商：老版本 config.ini 里存着旧地址，会盖过代码里的新默认值，
    /// 所以这里做一次性强制迁移（标记 [Network] api_migrated，避免每次都覆盖用户自定义的地址）。
    /// </summary>
    private static void MigrateApiServer()
    {
        try
        {
            const string tag = "2";
            if (Config.Get("Network", "api_migrated", "") == tag) return;
            var real = ApiSecrets.Base;
            if (!string.IsNullOrEmpty(real))
            {
                var old = Config.Get("Network", "api_base", "");
                if (!string.Equals(old, real, StringComparison.OrdinalIgnoreCase))
                {
                    Config.Set("Network", "api_base", real);
                    LogManager.Log("[API] 服务器地址已迁移到新供应商");
                }
            }
            else LogManager.Warn("[API] 编译期未注入服务器地址（本地构建缺 build.local.props）");
            Config.Set("Network", "api_migrated", tag);
        }
        catch (Exception e) { LogManager.Debug("API 迁移失败: " + e.Message); }
    }

    public static void Initialize()
    {
        Config = new AppConfig();

        // 日志：默认存放 %APPDATA%\netHEmusic\logs\log.txt（5 份轮换）
        LogManager.Init(Path.Combine(Config.DataDir, "logs"));

        MigrateApiServer();   // 换过 API 供应商：把老配置里存的旧地址强制换掉（只做一次）

        Lang = new LangService(Config, ResolveLangRoot());
        Netease = new NetEaseClient(Config.ApiBase);   // API 服务地址见 config.ini [Network] api_base
        Netease.SetCookie(Config.LoadCookie()); // 恢复登录态
        Cache = new CacheManager(Config);
        Cache.EnsureDir();
        Download = new DownloadManager(Netease, Config, new DownloadStore(Config));
        Updater = new UpdateManager(Config);
        SelfRepair = new SelfRepair(Config);
        Theme = new ThemeManager(Config);
        Player = new PlayerService(Config, Cache);
        Plugins = new Plugins.PluginService(Config);

        // 恢复上次的播放列表（只装载不播放：重启后 dock 与播放列表仍是上次的内容）
        try
        {
            var list = Config.LoadQueue();
            if (list is { Count: > 0 })
            {
                Player.RestoreQueue(list, Config.PlaylistIndex);
                LogManager.Log("已恢复播放列表: " + list.Count + " 首，当前第 " + (Config.PlaylistIndex + 1) + " 首");
            }
        }
        catch (Exception e) { LogManager.Debug("恢复播放列表失败: " + e.Message); }

        LogManager.Log("服务已初始化: 语言=" + Lang.CurrentLanguage + " theme=" + Config.Theme);
        LogManager.Log(Config.FirstRun ? "首次运行" : "非首次运行");
    }

    /// <summary>启动签名证书是否已信任（当前用户 Root / TrustedPublisher 中存在 LaoheTeam.top 或 Laohehehe）。</summary>
    public static bool CertificatePresent()
    {
        try
        {
            foreach (var n in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var s = new X509Store(n, StoreLocation.CurrentUser);
                try { s.Open(OpenFlags.ReadOnly); } catch { continue; }
                foreach (var c in s.Certificates)
                    if (c.Subject.Contains("CN=LaoheTeam.top", StringComparison.OrdinalIgnoreCase) ||
                        c.Subject.Contains("CN=Laohehehe", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch (Exception e) { LogManager.Error("证书检测异常: " + e.Message); }
        return false;
    }

    /// <summary>从内置资源安装并信任启动证书到当前用户 Root + TrustedPublisher。</summary>
    public static bool InstallCertificate()
    {
        try
        {
            var cerPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LaoheTeam.cer");
            if (!File.Exists(cerPath)) { LogManager.Warn("未找到内置证书: " + cerPath); return false; }
            var cert = new X509Certificate2(cerPath);
            foreach (var n in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var s = new X509Store(n, StoreLocation.CurrentUser);
                s.Open(OpenFlags.ReadWrite);
                s.Add(cert);
                s.Close();
            }
            LogManager.Log("启动证书已安装并信任");
            return true;
        }
        catch (Exception e) { LogManager.Error("安装证书失败: " + e.Message); return false; }
    }

    private static string ResolveLangRoot()
    {
        // 优先输出目录下的 lang，其次回退到源码仓库 lang 文件夹
        var outLang = Path.Combine(AppContext.BaseDirectory, "lang");
        if (Directory.Exists(outLang)) return outLang;
        // 项目根：从 bin 输出向上找 5 层
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(root); i++)
        {
            var candidate = Path.Combine(Path.GetFullPath(root), "lang");
            if (Directory.Exists(candidate)) return candidate;
            root = Path.GetDirectoryName(root);
        }
        return outLang;
    }
}
