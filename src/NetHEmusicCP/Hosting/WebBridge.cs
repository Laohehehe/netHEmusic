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
    private void PostToWeb(object msg) { try { WebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { } }

    /// <summary>把当前语言的词条表推给网页端（前端 i18n 用）。</summary>
    private void PushLang() => PostToWeb(new { type = "lang", code = AppServices.Lang.CurrentLanguage, table = AppServices.Lang.CurrentTable });
    private void PushTheme()
    {
        var (mode, pr, se, bg, dk) = AppServices.Theme.GetScheme();
        // Mica 打开时面板底色转半透明（界面不透明度设置，默认 78%），让窗口材质透上来
        double alpha = 1.0;
        if (AppServices.Config.Mica)
        {
            double.TryParse(AppServices.Config.Get("App", "ui_mica_alpha", "78"), out var a);
            alpha = Math.Max(0.55, Math.Min(1.0, (a > 0 ? a : 78) / 100.0));
        }
        PostToWeb(new { type = "theme", dark = mode == "dark", vars = ThemeManager.ToCssVars(mode, pr, se, bg, dk, alpha) });
    }
    private object ToSongDto(Song s) => new { s.Id, Title = s.Title, Artist = s.ArtistsName, Album = s.AlbumName, Pic = s.PicUrl, Duration = s.Duration };

    private Song? SongFromWeb(JsonElement s)
    {
        try
        {
            // 时长：前端 DTO 用 PascalCase 的 Duration，旧数据可能是 duration/dt，都要认
            long dur = 0;
            foreach (var key in new[] { "Duration", "duration", "dt", "DurationMs" })
                if (s.TryGetProperty(key, out var dv) && dv.ValueKind == JsonValueKind.Number) { dur = dv.GetInt64(); break; }
            var song = new Song
            {
                Id = s.TryGetProperty("Id", out var id) ? id.GetInt64() : 0,
                Title = s.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : (s.TryGetProperty("name", out var t2) ? t2.GetString() ?? "" : ""),
                Artists = new List<Artist> { new Artist { Name = s.TryGetProperty("Artist", out var a) ? a.GetString() ?? "" : "" } },
                Album = new Album { Name = s.TryGetProperty("Album", out var al) ? al.GetString() ?? "" : "", PicUrl = s.TryGetProperty("Pic", out var p) ? p.GetString() : "" },
                Pic = s.TryGetProperty("Pic", out var p2) ? p2.GetString() : "",
                DurationMs = dur
            };
            return song;
        }
        catch { return null; }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var doc = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
            var type = doc.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            LogManager.Debug("web msg: " + type);
            switch (type)
            {
                // 说明（2026-10-03 死代码清理）：原来的 case "search" / "playlist" / "lyric" / "login" / "dl_clear"
                // 都已经删掉 —— 网页侧现在统一走 type:"api" 的通用转发（js/api.js：cloudsearch / playlist/track/all /
                // lyric/new），登录 cookie 由 /api 自己拿；下载页只有「清除下载历史」(dl_history_clear) 和单条 dl_delete。
                case "discover": _ = HandleWebDiscover(); break;
                case "play": HandleWebPlay(doc); break;
                case "play_list": HandleWebPlayList(doc); break;
                case "download": HandleWebDownload(doc); break;
                case "dl_state": PostDownloadState(); break;
                case "dl_pause": HandleDownloadOp(doc, "pause"); break;
                case "dl_resume": HandleDownloadOp(doc, "resume"); break;
                case "dl_remove": HandleDownloadOp(doc, "remove"); break;
                case "dl_pause_all": AppServices.Download.PauseAll(); PostDownloadState(); break;
                case "dl_resume_all": AppServices.Download.ResumeAll(); PostDownloadState(); break;
                case "dl_list": PostDownloadedList(); break;
                case "dl_history_clear": AppServices.Download.ClearHistory(); PostDownloadedList(); PostToWeb(new { type = "toast", text = "下载历史已清除（音乐文件未删除）" }); break;
                case "dl_delete": HandleDeleteDownloaded(doc); break;
                case "dl_open_file": HandleOpenDownloaded(doc); break;
                case "plugins_list": HandlePluginsList(); break;
                case "plugin_code": HandlePluginCode(doc); break;
                case "plugin_toggle": HandlePluginToggle(doc); break;
                case "plugin_reload": HandlePluginReload(); break;
                case "plugins_open_dir": OpenPluginsDir(); break;
                case "plugin_data_read": HandlePluginData(doc, false); break;
                case "plugin_data_write": HandlePluginData(doc, true); break;
                case "plugin_http": _ = HandlePluginHttpAsync(doc); break;
                case "plugin_market": _ = HandlePluginMarketAsync(); break;
                case "plugin_install": _ = HandlePluginInstallAsync(doc); break;
                case "plugin_uninstall": HandlePluginUninstall(doc); break;
                case "login": break; // 历史遗留的空分支：登录由 Web 前端 via /api 完成（留着只为兼容老消息名）
                case "api": _ = HandleWebApi(doc); break;
                case "toggle": _ = AppServices.Player.ToggleAsync(); break;
                case "prev": _ = AppServices.Player.PrevAsync(); break;
                case "next": _ = AppServices.Player.NextAsync(); break;
                case "volume": { int.TryParse(doc.TryGetProperty("v", out var vv) ? vv.GetRawText() : "", out var n); AppServices.Player.SetVolume(n); break; }
                case "save_cookie": { var cookie = doc.TryGetProperty("cookie", out var ck) ? ck.GetString() ?? "" : ""; AppServices.Netease.SetCookie(cookie); AppServices.Config.SaveCookie(cookie); LogManager.Log("登录 cookie 已保存(脱敏)"); break; }
                case "queue_add": { if (doc.TryGetProperty("song", out var s2)) { var song = SongFromWeb(s2); if (song != null) { var q = AppServices.Player.Queue.ToList(); if (q.All(x => x.Id != song.Id)) { q.Add(song); _ = AppServices.Player.LoadQueueAsync(q, AppServices.Player.Index < 0 ? 0 : AppServices.Player.Index); } } } break; }
                case "playnext":
                    {
                        // 「下一首播放」：以前只要歌已在队列里就什么都不做（而大家点的往往就是列表里已有的歌）→ 点了没反应。
                        // 现在：已在队列里就【挪】到当前之后，不在就插入；两种都不产生重复，且不打断正在播的这首。
                        if (doc.TryGetProperty("song", out var s))
                        {
                            var song = SongFromWeb(s);
                            if (song != null)
                            {
                                var q = AppServices.Player.Queue.ToList();
                                int cur = AppServices.Player.Index;
                                int idx = q.FindIndex(x => x.Id == song.Id);
                                if (idx == cur || idx == cur + 1) { /* 正在播 / 已经是下一首 */ }
                                else
                                {
                                    if (idx >= 0) { q.RemoveAt(idx); if (idx < cur) cur--; }
                                    q.Insert(Math.Min(cur + 1, q.Count), song);
                                    AppServices.Player.RestoreQueue(q, cur);   // 不触发重播，避免正在听的歌被从头开始
                                    LogManager.Log("已把《" + song.Title + "》设为下一首播放（队列 " + q.Count + " 首）");
                                }
                            }
                        }
                        break;
                    }
                case "open_repo": OpenRepo(doc); break;
                case "get_settings": HandleGetSettings(); break;
                case "pick_folder": PickFolder(doc); break;
                case "open_folder": OpenDownloadDir(); break;
                case "release_notes": _ = HandleReleaseNotes(doc); break;
                case "notice_seen": AppServices.Config.VersionSeen = AppServices.Version; LogManager.Log("[Version] version 已更新为 " + AppServices.Version); break;
                case "queue_get": PostToWeb(new { type = "queue", songs = AppServices.Player.Queue.Select(ToSongDto).ToList(), index = AppServices.Player.Index }); break;
                case "play_index": { if (doc.TryGetProperty("index", out var qi) && qi.TryGetInt32(out var qn)) _ = AppServices.Player.PlayAtAsync(qn); break; }
                case "queue_clear": AppServices.Player.ClearQueue(); LogManager.Log("已清空播放列表"); break;
                case "audio_ended": _ = AppServices.Player.AutoNextAsync(); break;
                case "audio_state":
                    {
                        bool playing = doc.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.True;
                        long pos = doc.TryGetProperty("pos", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt64() : 0;
                        long dur = doc.TryGetProperty("dur", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetInt64() : 0;
                        AppServices.Player.SetFrontendState(playing, pos, dur);
                        _lastFrontPos = pos; _lastFrontDur = dur;
                        try { _lyricWin?.OnPosition(TimeSpan.FromMilliseconds(pos)); } catch { }   // 前端模式进度只从这里来
                        SaveResumeProgress(pos, dur, false);
                        break;
                    }
                case "audio_error":
                    {
                        var emsg = doc.TryGetProperty("message", out var em) ? (em.GetString() ?? "") : "";
                        int ecode = doc.TryGetProperty("code", out var ec) && ec.TryGetInt32(out var ecc) ? ecc : 0;
                        LogManager.Warn("前端播放失败(code=" + ecode + "): " + emsg);
                        _ = HandleAudioErrorAsync(ecode);
                        break;
                    }
                case "seek": { if (doc.TryGetProperty("pos", out var sk) && sk.TryGetInt64(out var skn)) AppServices.Player.Seek(TimeSpan.FromMilliseconds(skn)); break; }
                case "queue_remove": { if (doc.TryGetProperty("index", out var ri) && ri.TryGetInt32(out var rn)) AppServices.Player.RemoveAt(rn); break; }
                case "queue_next": { if (doc.TryGetProperty("index", out var ni) && ni.TryGetInt32(out var nn)) AppServices.Player.MoveToNext(nn); break; }
                case "share":
                    {
                        var song = doc.TryGetProperty("song", out var ss) ? SongFromWeb(ss) : null;
                        if (song is not null)
                        {
                            var text = "分享单曲《" + song.Title + "》- " + song.ArtistsName
                                     + "：" + "https://music.163.com/song?id=" + song.Id + " (@网易云音乐)";
                            var ok = ClipboardHelper.SetText(text);
                            PostToWeb(new { type = "toast", text = ok ? "分享链接已复制到剪贴板" : "复制失败（剪贴板被占用）" });
                            LogManager.Log("分享: " + (ok ? "已复制 " : "复制失败 ") + text);
                        }
                        break;
                    }
                case "copy_text":
                    {
                        // 前端拼好的任意文本（歌单分享链接等）→ 系统剪贴板
                        var txt = doc.TryGetProperty("text", out var ctv) ? ctv.GetString() ?? "" : "";
                        if (txt.Length > 0)
                        {
                            var ok = ClipboardHelper.SetText(txt);
                            PostToWeb(new { type = "toast", text = ok ? "分享链接已复制到剪贴板" : "复制失败（剪贴板被占用）" });
                            LogManager.Log("复制文本: " + (ok ? "成功 " : "失败 ") + txt);
                        }
                        break;
                    }
                case "queue_append":
                    {
                        // 批量加入播放列表：只追加、不打断当前播放（走 RestoreQueue，不触发重新解析）
                        if (doc.TryGetProperty("songs", out var arrA) && arrA.ValueKind == JsonValueKind.Array)
                        {
                            var q = AppServices.Player.Queue.ToList();
                            var added = 0;
                            foreach (var it in arrA.EnumerateArray())
                            {
                                var s = SongFromWeb(it);
                                if (s is null || s.Id <= 0) continue;
                                if (q.Any(x => x.Id == s.Id)) continue;
                                q.Add(s); added++;
                            }
                            if (added > 0)
                            {
                                AppServices.Player.RestoreQueue(q, AppServices.Player.Index);
                                PostToWeb(new { type = "toast", text = "已添加到播放列表 +" + added + " 首（共 " + q.Count + " 首）" });
                                LogManager.Log("追加播放列表: +" + added + " 首, 共 " + q.Count + " 首");
                            }
                            else PostToWeb(new { type = "toast", text = "这些歌都已经在播放列表里了" });
                        }
                        break;
                    }
                case "cache_put":
                    {
                        // 页面数据写进 temp 缓存（前端不用了就把内存里的副本丢掉，磁盘上还留着）
                        var ck = doc.TryGetProperty("key", out var ckv) ? ckv.GetString() ?? "" : "";
                        var cj = doc.TryGetProperty("json", out var cjv) ? cjv.GetString() ?? "" : "";
                        if (ck.Length > 0 && cj.Length > 0) AppServices.Cache.WriteText(ck, cj);
                        break;
                    }
                case "cache_get":
                    {
                        var ck2 = doc.TryGetProperty("key", out var ck2v) ? ck2v.GetString() ?? "" : "";
                        var txt = ck2.Length > 0 ? AppServices.Cache.ReadText(ck2) : null;
                        PostToWeb(new { type = "cache_data", key = ck2, json = txt });
                        break;
                    }
                case "cache_remove":
                    {
                        var ck3 = doc.TryGetProperty("key", out var ck3v) ? ck3v.GetString() ?? "" : "";
                        if (ck3.Length > 0) AppServices.Cache.Remove(ck3);
                        break;
                    }
                case "cache_clear":
                    AppServices.Cache.Clear();
                    PostToWeb(new { type = "toast", text = "页面缓存已清空" });
                    LogManager.Log("页面缓存已清空");
                    break;
                case "desktop_lyric": SetDesktopLyric(doc.TryGetProperty("on", out var dlOn) && dlOn.ValueKind == JsonValueKind.True); break;
                case "set_setting": HandleSetSetting(doc); break;
                case "app_exit": App.ExitApp(); break;   // 与托盘「退出」同一条路径（验证/自动化用）
                // 离线播放：告知前端哪些歌已经缓存到本地（断网时未缓存的置灰）
                // 前端上报在线/离线（navigator.onLine）：离线时 C# 侧不做预取/缓存/重试，避免请求风暴
                case "net_state":
                    {
                        bool off = doc.TryGetProperty("offline", out var offv) && offv.ValueKind == JsonValueKind.True;
                        netHEmusic.Core.Playback.PlayerService.Offline = off;
                        LogManager.Log("网络状态: " + (off ? "离线" : "在线"));
                        break;
                    }
                case "audio_cache_list":
                    PostToWeb(new { type = "audio_cache_list", ids = AppServices.Cache.AudioIds() });
                    break;
                case "log": LogManager.Info("web: " + (doc.TryGetProperty("msg", out var m) ? m.GetString() : "")); break;
            }
        }
        catch (Exception ex) { LogManager.Debug("Web 消息失败: " + ex.Message); }
    }

    /// <summary>通用 API 透传：前端按 name+args 调网易云接口，返回原始 JSON。</summary>
    private async Task HandleWebApi(JsonElement doc)
    {
        try
        {
            var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var args = new Dictionary<string, string?>();
            if (doc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                foreach (var p in a.EnumerateObject()) args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            bool post = doc.TryGetProperty("post", out var ps) && ps.GetBoolean();
            string raw = post ? await AppServices.Netease.PostRaw("/" + name.TrimStart('/'), args) : await AppServices.Netease.GetRaw("/" + name.TrimStart('/'), args);
            var id = doc.TryGetProperty("id", out var idp) ? idp.GetString() : "";
            LogManager.Log("api 响应: " + name + " len=" + raw.Length);
            PostToWeb(new { type = "api_result", id, name, data = JsonDocument.Parse(raw).RootElement });
        }
        catch (Exception e) { LogManager.Debug("api 透传失败: " + e.Message); PostToWeb(new { type = "api_result", id = doc.TryGetProperty("id", out var id2) ? id2.GetString() : "", name = doc.TryGetProperty("name", out var n2) ? n2.GetString() : "", error = e.Message }); }
    }
}