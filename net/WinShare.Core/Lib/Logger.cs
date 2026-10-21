// 移植自 electron/lib/logger.ts（统一应用日志系统：文件落盘 + 轮转 + console 镜像 + 尾部读取）。
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShare.Core.Lib;

/// <summary>
/// 应用日志（TS: logger.ts）。文件：%APPDATA%/WinSharePanel/logs/app.log（2MB 轮转 ×3）。
/// 便捷 API：Logger.Info(scope, ...args)——与 console 习惯一致（Error 渲染含堆栈，对象渲染为紧凑 JSON）。
/// 注意：镜像输出直接使用 Console，不得引入 Logger.*（避免自递归）。
/// </summary>
public static class Logger
{
    private static readonly string[] Levels = ["debug", "info", "warn", "error"];
    private const int MaxSize = 2 * 1024 * 1024;
    private const int MaxFiles = 3;
    private const int MaxLineLen = 8000;
    private const int TailCap = 1024 * 1024;
    private static readonly object Gate = new();
    private static string _cachedDir = "";
    // TS: fileEnabled = !('VITEST' in process.env)——vitest 运行默认关闭（防单测污染真实日志），
    // C# 侧额外检测 vstest 宿主标记，dotnet test 下同样默认关闭；测试用例经 SetFileLoggingForTesting 显式开启。
    private static bool _fileEnabled = !DetectTestHost();

    /// <summary>TS: logDir()（win32 → %APPDATA%/WinSharePanel/logs；否则 ~/.winshare-panel/WinSharePanel/logs）。</summary>
    public static string LogDir()
    {
        lock (Gate)
        {
            if (_cachedDir.Length > 0) return _cachedDir;
            string base_ = OperatingSystem.IsWindows()
                ? (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA"))
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Environment.GetEnvironmentVariable("APPDATA")!)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winshare-panel");
            _cachedDir = Path.Combine(base_, "WinSharePanel", "logs");
            return _cachedDir;
        }
    }

    /// <summary>测试专用：覆盖日志目录（与 __resetPoolForTesting 同惯例），null 恢复默认推导。</summary>
    public static void SetLogDirForTesting(string? dir)
    {
        lock (Gate) { _cachedDir = dir ?? ""; }
    }

    /// <summary>测试专用：开/关文件落盘（仅应在测试中调用）。</summary>
    public static void SetFileLoggingForTesting(bool enabled)
    {
        lock (Gate) { _fileEnabled = enabled; }
    }

    private static bool DetectTestHost()
    {
        string? vitest = Environment.GetEnvironmentVariable("VITEST");
        string? vstest = Environment.GetEnvironmentVariable("VSTEST");
        string? vstestConsole = Environment.GetEnvironmentVariable("VSTEST_CONSOLE");
        string? dshTest = Environment.GetEnvironmentVariable("DSH_TESTING");
        return !string.IsNullOrEmpty(vitest)
            || !string.IsNullOrEmpty(vstest)
            || !string.IsNullOrEmpty(vstestConsole)
            || !string.IsNullOrEmpty(dshTest);
    }

    private static string LogFile() => Path.Combine(LogDir(), "app.log");

    /// <summary>纯函数：格式化单行日志（供单测）。</summary>
    public static string FormatLine(string level, string ts, string scope, string text)
    {
        // TS: text.replace(/\s+$/, '').slice(0, MAX_LINE_LEN) —— 先剥离尾部空白，再对"已剥离的串"截断到 8000
        string clean = text.TrimEnd();
        if (clean.Length > MaxLineLen)
        {
            clean = clean[..MaxLineLen];
        }
        return $"[{ts}] [{level.ToUpperInvariant()}] [{scope}] {clean}";
    }

    /// <summary>校验并规整外部来源的 level，非法回退 info（与 F-1 边界守卫同风格）。</summary>
    public static string NormalizeLevel(object? v) =>
        v is string s && Levels.Contains(s) ? s : "info";

    /// <summary>写一条日志：console 镜像 + 文件全量；任何 IO 失败静默降级为仅 console。</summary>
    public static void WriteLog(string level, string scope, string text)
    {
        string line = FormatLine(level, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), scope, text);
        switch (level)
        {
            case "error":
                Console.Error.WriteLine(line);
                break;
            case "warn":
                Console.Error.WriteLine(line);
                break;
            default:
                Console.WriteLine(line);
                break;
        }
        try
        {
            if (!FileEnabled) return;
            Directory.CreateDirectory(LogDir());
            string file = LogFile();
            if (File.Exists(file) && new FileInfo(file).Length > MaxSize) Rotate(file);
            File.AppendAllText(file, line + "\n");
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    private static bool FileEnabled
    {
        get { lock (Gate) { return _fileEnabled; } }
    }

    private static void Rotate(string file)
    {
        try
        {
            // 触顶删除最旧归档，然后 .2→.3、.1→.2、app.log→.1
            if (File.Exists($"{file}.{MaxFiles}")) File.Delete($"{file}.{MaxFiles}");
            for (int i = MaxFiles - 1; i >= 1; i--)
            {
                if (File.Exists($"{file}.{i}")) File.Move($"{file}.{i}", $"{file}.{i + 1}");
            }
            if (File.Exists(file)) File.Move(file, file + ".1");
        }
        catch
        {
            /* 轮转失败不阻塞写入 */
        }
    }

    /// <summary>读取尾部 N 行（UI"应用日志"页与排障复制用）；文件不存在返回空串。</summary>
    public static string ReadLogTail(int lines = 300)
    {
        try
        {
            string file = LogFile();
            if (!File.Exists(file)) return "";
            string content = File.ReadAllText(file);
            // 大文件仅截尾部 1MB 再取行，避免全量进内存
            if (content.Length > TailCap)
            {
                content = content[^TailCap..];
                int nl = content.IndexOf('\n');
                if (nl >= 0) content = content[(nl + 1)..];
            }
            var arr = content
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Where(l => l.Length > 0)
                .ToList();
            int n = Math.Max(1, lines);
            int start = Math.Max(0, arr.Count - n);
            return string.Join("\n", arr.Skip(start));
        }
        catch
        {
            return "";
        }
    }

    // 便捷 API：Logger.Info("smb", "msg", obj) —— 与 console 习惯一致

    public static void Debug(string scope, params object?[] args) => WriteLog("debug", scope, RenderArgs(args));
    public static void Info(string scope, params object?[] args) => WriteLog("info", scope, RenderArgs(args));
    public static void Warn(string scope, params object?[] args) => WriteLog("warn", scope, RenderArgs(args));
    public static void Error(string scope, params object?[] args) => WriteLog("error", scope, RenderArgs(args));

    /// <summary>
    /// TS: args.map(a => a instanceof Error ? (a.stack ?? a.message) : util.format(a)).join(' ')。
    /// C# 对应：Exception → ToString()（含堆栈）；引用类型 → 紧凑 JSON；其余 → 不变文化字符串形式。
    /// </summary>
    private static string RenderArgs(object?[] args) =>
        string.Join(" ", args.Select(RenderArg));

    /// <summary>
    /// 单参渲染（TS util.format 等价）：string 原样；bool 小写（JS String(true)="true"）；
    /// 数值/枚举/日期等标量 → 不变文化字符串；Exception → ToString（含堆栈）；其余引用类型 → 紧凑 JSON。
    /// 注意：本编译面（SMA runtime 程序集与 BCL ref pack 混用）下接口模式匹配（如 is IFormattable）
    /// 恒 False，故用具体类型 + Type 判断，不用接口模式。
    /// </summary>
    private static string RenderArg(object? a)
    {
        switch (a)
        {
            case null:
                return "";
            case Exception e:
                return e.ToString() ?? e.Message;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
        }
        Type t = a.GetType();
        if (t.IsPrimitive || t.IsEnum
            || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan) || t == typeof(Guid))
        {
            return Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
        }
        return RenderJson(a);
    }

    /// <summary>引用类型 → 紧凑 JSON（TS util.format 对象渲染等价；POCO/record/集合均可）。</summary>
    private static string RenderJson(object value)
    {
        using var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, value, value.GetType());
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
