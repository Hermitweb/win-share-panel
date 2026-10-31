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
        // 四个源互相独立，串行 await 会让总耗时变成「求和」（实测整轮 ~6-7s）。
        // 并行后总耗时降到最慢的那一个。单源失败不影响其他源（如实跳过，对齐 TS per-query error）。
        var statsTask = _system is { } s ? s.GetDashboardStatsAsync() : Task.FromResult<DashboardStats?>(null);
        var disksTask = _disk is { } d ? d.GetDiskUsagesAsync() : Task.FromResult<IReadOnlyList<DiskUsage>?>(null);
        var smbTask = _smb is { } m ? m.GetConfigAsync() : Task.FromResult<SmbServerConfig?>(null);
        var secTask = _security is { } x ? x.GetSecurityReportAsync() : Task.FromResult<SecurityReport?>(null);

        try { await Task.WhenAll(statsTask, disksTask, smbTask, secTask); }
        catch { /* 单源异常已由 IsCompletedSuccessfully 判为 null，不影响其他源 */ }

        return new DashboardBundle
        {
            Stats = statsTask.IsCompletedSuccessfully ? statsTask.Result : null,
            Disks = disksTask.IsCompletedSuccessfully ? disksTask.Result : null,
            SmbConfig = smbTask.IsCompletedSuccessfully ? smbTask.Result : null,
            Security = secTask.IsCompletedSuccessfully ? secTask.Result : null,
        };
    }
}

/// <summary>空馈送（服务层未接前默认）：全 null，界面走空态不崩。</summary>
public sealed class NoopDashboardFeed : IDashboardDataFeed
{
    public Task<DashboardBundle> GetAsync() =>
        Task.FromResult(new DashboardBundle { });
}
