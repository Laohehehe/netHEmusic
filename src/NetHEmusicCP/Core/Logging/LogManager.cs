using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace netHEmusic.Core.Logging;

/// <summary>
/// 详细日志系统：默认 log.txt，启动时轮换为 5 份（log.txt→log1..log5，超出删除）。
/// 提供控制台（-debugger 与设置里的控制台开关）输出；敏感信息脱敏。
/// </summary>
public static class LogManager
{
    private static readonly object _lock = new object();
    private static string _dir = "";
    private static bool _consoleEnabled = false;
    private static readonly List<string> _consoleBuffer = new();

    /// <summary>初始化日志目录并执行 5 份轮换。</summary>
    public static void Init(string directory)
    {
        lock (_lock)
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
            Rotate();
            WriteFile("----- netHEmusic 日志开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " -----");
        }
    }

    /// <summary>启动时轮换日志：log.txt→log1→...→log5，log5(存在则删除)。</summary>
    private static void Rotate()
    {
        // 从最旧到最新移动：log5 删除 -> log4->log5 -> log3->log4 -> log2->log3 -> log1->log2 -> log.txt->log1
        string last = Path.Combine(_dir, "log5.txt");
        if (File.Exists(last)) TryDelete(last);
        for (int i = 4; i >= 1; i--)
        {
            string src = Path.Combine(_dir, "log" + i + ".txt");
            string dst = Path.Combine(_dir, "log" + (i + 1) + ".txt");
            if (File.Exists(src)) TryMove(src, dst);
        }
        string cur = Path.Combine(_dir, "log.txt");
        if (File.Exists(cur)) TryMove(cur, Path.Combine(_dir, "log1.txt"));
    }

    private static void TryMove(string s, string d) { try { File.Move(s, d, true); } catch { } }
    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    /// <summary>开启/关闭实时控制台输出。</summary>
    public static void SetConsoleEnabled(bool enabled)
    {
        lock (_lock)
        {
            _consoleEnabled = enabled;
            if (enabled)
            {
                // 把启动缓存回放
                foreach (var line in _consoleBuffer) Console.WriteLine(line);
                _consoleBuffer.Clear();
            }
        }
    }

    /// <summary>记录 INFO 级日志。</summary>
    public static void Log(string msg) => Write("INFO", msg);

    /// <summary>记录 INFO 级日志（语义别名）。</summary>
    public static void Info(string msg) => Write("INFO", msg);

    /// <summary>记录 DEBUG 级日志。</summary>
    public static void Debug(string msg) => Write("DEBUG", msg);

    /// <summary>记录警告。message 可为对象或异常。</summary>
    public static void Warn(string msg) => Write("WARN", msg);

    /// <summary>记录错误。可传异常。</summary>
    public static void Error(string msg, Exception? ex = null)
    {
        Write("ERROR", msg + (ex != null ? " :: " + ex : ""));
    }

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Sanitize(msg)}";
        lock (_lock)
        {
            WriteFile(line);
            if (_consoleEnabled) { try { Console.WriteLine(line); } catch { } }
            else _consoleBuffer.Add(line);
        }
    }

    private static void WriteFile(string line)
    {
        if (string.IsNullOrEmpty(_dir)) return;
        try
        {
            File.AppendAllText(
                Path.Combine(_dir, "log.txt"),
                line + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { /* 日志写入失败不抛出 */ }
    }

    /// <summary>敏感信息脱敏：token / cookie / password 等值替换为 XlogintokenX。</summary>
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 脱敏常见的认证字段值
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"(?i)(token|cookie|password|passwd|secret|authorization|sessionid)[=:]\s*[\w\-\+\./=]{6,}",
            "$1=XlogintokenX");
        return s;
    }
}
