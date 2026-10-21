// 移植自 electron/lib/audit.ts（审计日志：JSON 行，5MB ×10 轮转；失败静默不影响主流程）。
using System.Text.Json.Nodes;

namespace WinShare.Core.Lib;

/// <summary>
/// 审计日志（TS: audit.ts）。文件：%APPDATA%/WinSharePanel/audit.log（5MB 轮转 ×10）。
/// 每行一个 JSON 记录 { ts, operator, action, target, result, detail? }。
/// </summary>
public static class Audit
{
    /// <summary>TS: result='success'。</summary>
    public const string Success = "success";
    /// <summary>TS: result='failure'。</summary>
    public const string Failure = "failure";

    private const int MaxSize = 5 * 1024 * 1024;
    private const int MaxFiles = 10;
    private static readonly object Gate = new();
    private static string? _dirOverride;

    /// <summary>测试专用：覆盖审计目录（与 Logger.SetLogDirForTesting 同惯例），null 恢复默认推导。</summary>
    public static void SetDirForTesting(string? dir)
    {
        lock (Gate) { _dirOverride = dir; }
    }

    /// <summary>TS: logDir()（win32 → %APPDATA%/WinSharePanel；否则 ~/.winshare-panel）。</summary>
    public static string AuditDir()
    {
        lock (Gate)
        {
            if (_dirOverride is not null) return _dirOverride;
            string base_ = OperatingSystem.IsWindows()
                ? (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA"))
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Environment.GetEnvironmentVariable("APPDATA")!)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winshare-panel");
            return Path.Combine(base_, "WinSharePanel");
        }
    }

    private static string LogFile() => Path.Combine(AuditDir(), "audit.log");

    /// <summary>
    /// TS: audit(operator, action, target, result, detail?)——写一行 JSON 并落盘；
    /// 触顶轮转（.1→.2 … 再 file→.1）；任何 IO 失败静默降级。
    /// </summary>
    public static void Write(string @operator, string action, string target, string result, string? detail = null)
    {
        try
        {
            string dir = AuditDir();
            Directory.CreateDirectory(dir);
            string file = LogFile();
            if (File.Exists(file) && new FileInfo(file).Length > MaxSize) Rotate(file);
            var record = new JsonObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["operator"] = @operator,
                ["action"] = action,
                ["target"] = target,
                ["result"] = result,
            };
            if (detail is not null)
            {
                record["detail"] = detail;
            }
            File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(record) + "\n");
        }
        catch
        {
            // 审计失败不影响主流程
        }
    }

    /// <summary>TS: rotate(file)（.9→.10 … .1→.2，file→.1；最旧 .10 直接覆盖，不预删）。</summary>
    private static void Rotate(string file)
    {
        for (int i = MaxFiles - 1; i >= 1; i--)
        {
            if (File.Exists($"{file}.{i}")) File.Move($"{file}.{i}", $"{file}.{i + 1}");
        }
        if (File.Exists(file)) File.Move(file, file + ".1");
    }

    /// <summary>TS: readAuditLog()——整读 audit.log；文件不存在/读取失败返回空串。</summary>
    public static string ReadAuditLog()
    {
        try
        {
            string file = LogFile();
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }
        catch
        {
            return "";
        }
    }
}
