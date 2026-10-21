using System.IO;

namespace WinShare.App.Shell;

/// <summary>
/// 应用日志（对齐 electron/lib/logger.ts 的 logDir 语义：%APPDATA%\WinSharePanel\logs\app.log）。
/// WPF 侧不做轮转底座（文件日志按 append；2MB×3 轮转由后续服务层任务承接），
/// 这里承担 crashReporter 替代 + 架构/运行痕迹可追溯的最小面。
/// </summary>
public static class LogSink
{
    static readonly object Gate = new();
    static string? _dirOverride;

    public static string LogDir()
    {
        if (_dirOverride is { } d) return d;
        var baseDir = Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, "WinSharePanel", "logs");
    }

    /// <summary>测试钩子：日志目录重定向。</summary>
    public static void SetDirForTesting(string dir)
    {
        lock (Gate) _dirOverride = dir;
    }

    public static void Info(string scope, string message) => Write("info", scope, message);
    public static void Warn(string scope, string message) => Write("warn", scope, message);
    public static void Error(string scope, string message) => Write("error", scope, message);

    static void Write(string level, string scope, string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = LogDir();
                Directory.CreateDirectory(dir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{scope}] {message}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(dir, "app.log"), line);
            }
        }
        catch
        {
            // 日志写失败静默（不影响主流程）
        }
    }
}
