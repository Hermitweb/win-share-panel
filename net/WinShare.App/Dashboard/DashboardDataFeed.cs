using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>
/// 仪表板数据束（对齐 Dashboard.tsx 的 4 个 react-query 源 + 不可用时如实 null）：
/// - Stats：getDashboardStats（ISystemService）
/// - Disks：disk.usages（IDiskService）
/// - SmbConfig：smb.getConfig（ISmbService）
/// - Security：security.report（ISecurityService）
/// null 字段 = TS skippedSources 语义（"部分检查源不可用，已如实跳过"）。
/// </summary>
public sealed record DashboardBundle
{
    public DashboardStats? Stats { get; init; }
    public IReadOnlyList<DiskUsage>? Disks { get; init; }
    public SmbServerConfig? SmbConfig { get; init; }
    public SecurityReport? Security { get; init; }
}

/// <summary>
/// 仪表板数据馈送抽象（可测试接缝）：ViewModel 只依赖它，
/// 生产实现 = ContractServicesDataFeed（按契约服务）；测试注入假束。
/// </summary>
public interface IDashboardDataFeed
{
    Task<DashboardBundle> GetAsync();
}

/// <summary>
/// 契约服务馈送：各源独立 try/catch（对齐 TS per-query error → skippedSources），
/// 未注入的服务保持 null。T3b/c/d 服务实现落地后在组合根注入即自动点亮。
/// </summary>
public sealed class ContractServicesDataFeed : IDashboardDataFeed
{
    readonly ISystemService? _system;
    readonly IDiskService? _disk;
    readonly ISmbService? _smb;
    readonly ISecurityService? _security;

    public ContractServicesDataFeed(ISystemService? system = null, IDiskService? disk = null,
        ISmbService? smb = null, ISecurityService? security = null)
    {
        _system = system;
        _disk = disk;
        _smb = smb;
        _security = security;
    }

    public async Task<DashboardBundle> GetAsync()
    {
        DashboardStats? stats = null;
        try { if (_system is { } s) stats = await s.GetDashboardStatsAsync(); }
        catch { stats = null; } // 单源失败不影响其他源（如实跳过）

        IReadOnlyList<DiskUsage>? disks = null;
        try { if (_disk is { } d) disks = await d.GetDiskUsagesAsync(); }
        catch { disks = null; }

        SmbServerConfig? smb = null;
        try { if (_smb is { } m) smb = await m.GetConfigAsync(); }
        catch { smb = null; }

        SecurityReport? sec = null;
        try { if (_security is { } x) sec = await x.GetSecurityReportAsync(); }
        catch { sec = null; }

        return new DashboardBundle
        {
            Stats = stats,
            Disks = disks,
            SmbConfig = smb,
            Security = sec,
        };
    }
}

/// <summary>空馈送（服务层未接前默认）：全 null，界面走空态不崩。</summary>
public sealed class NoopDashboardFeed : IDashboardDataFeed
{
    public Task<DashboardBundle> GetAsync() =>
        Task.FromResult(new DashboardBundle { });
}
