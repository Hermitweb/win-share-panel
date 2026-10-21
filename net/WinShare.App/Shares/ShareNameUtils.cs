using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享名/盘符/磁盘水位纯函数（逐条对齐 src/utils/shareDefaults.ts；xUnit 直测）：
/// - ShareNameFromPath ≙ shareNameFromPath（末级目录名，非法字符转下划线，限 60 字符）
/// - DriveOf/IsUncPath ≙ driveOf/isUncPath
/// - IndexDiskUsages ≙ indexDiskUsages（按盘符大写索引，O(1) 取水位）
/// 磁盘水位判定复用 Dashboard 的 AlertEvaluator.DiskLevel（TS diskLevel 单一实现）。
/// </summary>
public static class ShareNameUtils
{
    /// <summary>TS: DEFAULT_READ_ACCESS = ['Users']——默认授权安全下限。</summary>
    public static readonly string[] DefaultReadAccess = ["Users"];

    /// <summary>TS shareNameFromPath：盘符根（"D:"）不是文件夹名，返回空让调用方保留用户输入。</summary>
    public static string ShareNameFromPath(string? p)
    {
        var trimmed = string.IsNullOrEmpty(p) ? "" : p!.Trim();
        // 去尾部分隔符后按 / 或 \ 切分
        var noTrail = Regex.Replace(trimmed, @"[\\/]+$", "");
        var parts = noTrail.Split(new[] { '\\', '/' }, StringSplitOptions.None);
        var baseName = (parts.Length > 0 ? parts[^1] : "").Trim();
        // 盘符根形态（"D:"/"d"，≤2 字符且形如 X: 或 X）不是文件夹名
        if (baseName.Length <= 2 && Regex.IsMatch(baseName, "^[A-Za-z]:?$")) return "";
        var value = Regex.Replace(baseName, @"^[.$]+", "");                 // 先剥前导点/$（隐藏共享形态）
        value = Regex.Replace(value, "[<>:\"|?*\\\\/$]", "_");            // 非法字符转下划线
        return value.Length > 60 ? value[..60] : value;                     // 限 60 字符
    }

    /// <summary>TS driveOf："E:\a\b" → "E:"；非本地路径返回 null。</summary>
    public static string? DriveOf(string? p)
    {
        var m = Regex.Match(string.IsNullOrEmpty(p) ? "" : p!, "^([A-Za-z]):");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() + ":" : null;
    }

    /// <summary>TS isUncPath：UNC（\\server\share）没有本地盘符，不显示水位。</summary>
    public static bool IsUncPath(string? p) =>
        Regex.IsMatch(string.IsNullOrEmpty(p) ? "" : p!, @"^\\\\[^\\]");

    /// <summary>TS indexDiskUsages：按盘符建索引（大写归一）。</summary>
    public static Dictionary<string, DiskUsage> IndexDiskUsages(IReadOnlyList<DiskUsage>? usages)
    {
        var map = new Dictionary<string, DiskUsage>();
        if (usages is null) return map;
        foreach (var u in usages)
            if (!string.IsNullOrEmpty(u.drive))
                map[u.drive.ToUpperInvariant()] = u;
        return map;
    }

    /// <summary>
    /// TS waterLevelOf：所在盘水位标记。UNC/无盘符/查不到用量的盘一律返回 null——
    /// 没有本地卷就没有"剩余空间"语义，显示 0% 或空标记都会误导运维。
    /// 仅 warn/danger 返回（ok 不打扰，对齐 TS 调用方过滤语义）。
    /// </summary>
    public static (DiskUsage usage, string level)? WaterLevelOf(
        string path, Dictionary<string, DiskUsage> diskIndex, int diskLowGb)
    {
        if (IsUncPath(path)) return null;
        var drive = DriveOf(path);
        if (drive is null || !diskIndex.TryGetValue(drive, out var usage)) return null;
        var level = State.AlertEvaluator.DiskLevel(usage, 10, diskLowGb);
        return level is "warn" or "danger" ? (usage, level) : null;
    }
}
