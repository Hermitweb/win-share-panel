using System.IO;
using System.Text.RegularExpressions;
using WinShare.App.State;
using WinShare.App.Shell;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 应用日志 / 审计日志查看器 VM（等宽滚动 + 级别/结果过滤；对齐 electron 版 LogPanels 语义）。
/// [Source: src/components/LogPanels.tsx + net/WinShare.App/Shell/LogSink.cs]
/// 应用日志：app.log 最近 300 行（LogSink 行格式「时间 [level] [scope] message」），级别过滤；
/// 审计日志：stateStore journal 台账最近 200 条（与 TS「最近 200 条操作审计」同窗口），结果过滤。
/// </summary>
public static class AppLogReader
{
    /// <summary>app.log 路径（测试可注入目录；默认 LogSink.LogDir()）。</summary>
    public static string LogPath(string? dirOverride = null)
        => Path.Combine(dirOverride ?? LogSink.LogDir(), "app.log");

    /// <summary>读最近 N 行（文件缺失 → 空串；读失败不抛——对齐 TS tail().catch(() => '')）。</summary>
    public static string ReadTail(int lines = 300, string? dirOverride = null)
    {
        var path = LogPath(dirOverride);
        try
        {
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>级别过滤（"all"/"info"/"warn"/"error"；解析不出级别且过滤非 all → 隐藏该行，防脏行穿透）。</summary>
    public static string FilterByLevel(string text, string level)
    {
        if (string.IsNullOrEmpty(text) || level == "all") return text ?? "";
        var kept = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line, @"\[(info|warn|error)\]");
            if (m.Success && m.Groups[1].Value == level) kept.Add(line);
        }
        return string.Join(Environment.NewLine, kept);
    }
}

/// <summary>审计行（对齐 TS AuditRow：时间/操作/对象/结果/原因；解析失败行原样保留不丢内容）。</summary>
public sealed record AuditRow
{
    public required string Key { get; init; }
    public string? Time { get; init; }
    public string? Action { get; init; }
    public string? Target { get; init; }
    public string Result { get; init; } = "";
    public string? Detail { get; init; }
    /// <summary>true=非结构行（原样展示）。</summary>
    public bool Raw { get; init; }
}

public sealed class AuditLogViewModel : SettingsCardBase
{
    readonly AppSettingsStore _store;

    public IReadOnlyList<AuditRow> Rows { get; private set; } = Array.Empty<AuditRow>();

    public AuditLogViewModel(AppSettingsStore store)
    {
        _store = store;
        Reload();
        Raise();
    }

    public void Reload()
    {
        var journal = _store.Load().journal;
        var slice = journal.Skip(Math.Max(0, journal.Count - AppSettingsStore.JournalMax)).ToList();
        Rows = slice.Select((e, i) => new AuditRow
        {
            Key = e.id,
            Time = e.ts > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : null,
            Action = ActionLabel(e.action),
            Target = $"{e.protocol.Display()}：{e.name}",
            Result = e.undoable ? "可撤销" : "已应用",
            Detail = e.detail,
        }).ToList();
    }

    /// <summary>journal action → 展示名（对齐 TS 审计「操作」列语义；与日志台账小写枚举一致）。</summary>
    public static string ActionLabel(JournalAction a) => a switch
    {
        JournalAction.Create => "创建",
        JournalAction.Delete => "删除",
        JournalAction.ToggleOff => "停止",
        JournalAction.Permset => "改权限",
        _ => a.ToString(),
    };

    /// <summary>结果过滤（"all"/"可撤销"/"已应用"；对齐任务验收「级别过滤」在审计侧的落点）。</summary>
    public IReadOnlyList<AuditRow> Filter(string resultFilter)
    {
        if (resultFilter is "all" or null or "") return Rows;
        return Rows.Where(r => r.Result == resultFilter).ToList();
    }
}

/// <summary>应用日志查看器 VM（读取 + 级别过滤 + 复制/导出文案面）。</summary>
public sealed class AppLogViewModel : SettingsCardBase
{
    public const int TailLines = 300;

    readonly string? _dirOverride;

    public AppLogViewModel(string? dirOverride = null)
    {
        _dirOverride = dirOverride;
        Error = null;
        Load();
        Raise();
    }

    public IReadOnlyList<string> Lines { get; private set; } = Array.Empty<string>();

    /// <summary>级别过滤（对齐 electron 版「读 app.log 最近 300 行 + 可筛选」语义）。</summary>
    public IReadOnlyList<string> Filtered(string levelFilter)
    {
        var text = string.Join(Environment.NewLine, Lines);
        var filtered = AppLogReader.FilterByLevel(text, levelFilter);
        return string.IsNullOrEmpty(filtered) ? Array.Empty<string>()
            : filtered.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public void Load()
    {
        var text = AppLogReader.ReadTail(TailLines, _dirOverride);
        Lines = string.IsNullOrEmpty(text) ? Array.Empty<string>()
            : text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        Raise(nameof(Lines));
    }

    public string RawText => string.Join(Environment.NewLine, Lines);
}
