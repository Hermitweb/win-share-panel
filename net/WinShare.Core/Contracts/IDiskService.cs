namespace WinShare.Core.Contracts;

/// <summary>
/// 磁盘水位与共享根建议（对应 electron/services/disk.ts 导出：
/// getDiskUsages / driveOfPath / isUnc / suggestShareRoot / suggestRoot）。
/// </summary>
public interface IDiskService
{
    Task<IReadOnlyList<DiskUsage>> GetDiskUsagesAsync();

    /// <summary>TS: driveOfPath(p)（同步），取路径所在盘符，UNC/非法返回 null。</summary>
    string? DriveOfPath(string p);

    /// <summary>TS: isUnc(p)（同步）。</summary>
    bool IsUnc(string p);

    /// <summary>TS: suggestShareRoot(usages, userHome='')（同步），按剩余空间给共享根建议。</summary>
    string SuggestShareRoot(IReadOnlyList<DiskUsage> usages, string userHome = "");

    /// <summary>TS: suggestRoot()（异步，内部取 usages + 用户主目录）。</summary>
    Task<string> SuggestRootAsync();
}
