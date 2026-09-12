using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Localization;

/// <summary>
/// 本地化系统：从 lang\<code>.lang 文件加载键值。支持 zh_cn / en_US。
/// 键名形如 text.main.headbar.search ；未匹配回退英文，再回退键自身。
/// </summary>
public sealed class LangService
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private readonly string _langRoot;
    private string _current = "zh_cn";

    public event Action? LanguageChanged;
    public string CurrentLanguage => _current;

    public LangService(AppConfig config, string langRoot)
    {
        _langRoot = langRoot;
        LoadAll();
        _current = config.Language;
        if (!_tables.ContainsKey(_current)) _current = "zh_cn";
    }

    private void LoadAll()
    {
        if (!Directory.Exists(_langRoot)) return;
        foreach (var f in Directory.EnumerateFiles(_langRoot, "*.lang"))
        {
            var code = Path.GetFileNameWithoutExtension(f);
            _tables[code] = Parse(f);
        }
        // 兜底内置表
        _tables.TryAdd("zh_cn", new());
        _tables.TryAdd("en_US", new());
    }

    private static Dictionary<string, string> Parse(string file)
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                var idx = t.IndexOf('=');
                if (idx > 0) d[t[..idx].Trim()] = t[(idx + 1)..].Trim();
            }
        }
        catch { }
        return d;
    }

    public void SetLanguage(string code)
    {
        if (!_tables.ContainsKey(code)) return;
        _current = code;
        LogManager.Log("语言切换为 " + code);
        LanguageChanged?.Invoke();
    }

    /// <summary>取翻译。支持 {0} 占位符参数。</summary>
    public string T(string key, params object?[] args)
    {
        var table = _tables.TryGetValue(_current, out var cur) ? cur : null;
        string v = key;
        if (table is not null && table.TryGetValue(key, out var x)) v = x;
        else if (_tables.TryGetValue(_current == "zh_cn" ? "en_US" : "zh_cn", out var alt) && alt.TryGetValue(key, out var y)) v = y;
        if (args.Length > 0) try { v = string.Format(v, args); } catch { }
        return v;
    }

    public string this[string key] => T(key);

    /// <summary>内建两套语言的基础词条（供兜底与语言包合并）。</summary>
    public void EnsureBuiltin(string code, Dictionary<string, string> entries)
    {
        if (_tables.TryGetValue(code, out var t)) foreach (var kv in entries) if (!t.ContainsKey(kv.Key)) t[kv.Key] = kv.Value;
        else _tables[code] = new Dictionary<string, string>(entries);
    }
}
