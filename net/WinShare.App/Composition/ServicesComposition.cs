// 组合根：把 Core 契约服务装配成一套实例，注入 UI 侧静态槽（App 各模块的注入缝）。
// 设计口径（用户确认）：静态槽位注入，不改 AppServices 为 DI 容器。
// 依赖顺序：ps 通道 → 基础服务 → 跨域服务（Diagnose 依赖 smb/user/firewall）→ 适配器 → registry。
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Host;
using WinShare.Core.Services.Misc;
using WinShare.Core.Services.Protocols;
using WinShare.Core.Services.Protocols.Adapters;
using WinShare.Core.Services.ProtocolServices;
using WinShare.Core.Services.Shares;
using WinShare.Core.Services.Users;

namespace WinShare.App.Composition;

/// <summary>装配结果（供组合根把实例喂给仪表板馈送与趋势采样器）。</summary>
public sealed record ComposedServices
{
    public required ISystemService System { get; init; }
    public required IDiskService Disk { get; init; }
    public required ISmbService Smb { get; init; }
    public required ISecurityService Security { get; init; }
    public required IShareService Share { get; init; }
    public required IUserService User { get; init; }
    public required ISessionService Session { get; init; }
    public required IPresetService Preset { get; init; }
    public required IProtocolService Protocol { get; init; }
    public required IReadOnlyDictionary<Protocol, IProtocolAdapter> Adapters { get; init; }
    public required IFirewallService Firewall { get; init; }
    public required IDiagnoseService Diagnose { get; init; }
    public required IUpdateService Update { get; init; }
    public required IPathRegistrationService PathRegistration { get; init; }
    public required ITransferService Transfer { get; init; }
}

/// <summary>
/// 服务装配器：构造全部 Core 服务并写入 UI 静态槽。
/// 任一服务构造失败不会中断装配（WPF 侧仍可运行，对应模块走降级）。
/// </summary>
public static class ServicesComposition
{
    /// <summary>装配并注入静态槽；返回装配结果供调用方接线仪表板/采样器。</summary>
    public static ComposedServices Build()
    {
        IPsInvoker ps = PsRunner.Default;
        IStateStore store = StateStore.Default;

        // === 基础服务 ===
        var share = new ShareService(ps, store);
        var smb = new SmbService(ps);
        var session = new SessionService(ps);
        var user = new UserService(ps, store);
        var preset = new PresetService(user);

        // === 系统域 ===
        var disk = new DiskService(ps);
        var security = new SecurityService(ps);
        var firewall = new FirewallService(ps);

        // === 协议服务（配置面）===
        var nfs = new NfsService(ps);
        var ftp = new FtpService(ps);
        var webdav = new WebdavService(ps);

        // === 协议适配器（列表/创建/权限/会话面；配置面委托上面三个服务）===
        var adapters = new Dictionary<Protocol, IProtocolAdapter>
        {
            [Protocol.Smb] = new SmbAdapter(share, session, smb, user),
            [Protocol.Nfs] = new NfsAdapter(nfs),
            [Protocol.Ftp] = new FtpAdapter(ftp),
            [Protocol.Webdav] = new WebdavAdapter(webdav),
        };
        var protocol = new ProtocolRegistry(adapters.Values, ps);

        // === 依赖跨域的聚合服务 ===
        var system = new SystemService(ps, smb, protocol, store);
        var diagnose = new DiagnoseService(ps, smb, user, firewall);
        var update = new UpdateService();
        var pathReg = new PathRegistrationService(AppContext.BaseDirectory, isPackaged: true, ps);
        var transfer = new TransferService(store);
        var journal = new JournalService(store, share, user);

        // === 注入 UI 静态槽 ===
        Shares.SharesServices.Share = share;
        Shares.SharesServices.Preset = preset;
        Shares.SharesServices.User = user;
        Shares.SharesServices.Disk = disk;
        Shares.SharesServices.Protocol = protocol;

        Users.UsersModule.UserService = user;
        Users.UsersModule.ShareService = share;

        Sessions.SessionsModule.SessionService = session;
        Sessions.SessionsModule.Adapters = adapters;

        Settings.SettingsServiceSlots.ProtocolService = protocol;
        Settings.SettingsServiceSlots.UpdateService = update;
        Settings.SettingsServiceSlots.FirewallService = firewall;
        Settings.SettingsServiceSlots.SecurityService = security;
        Settings.SettingsServiceSlots.DiagnoseService = diagnose;
        Settings.SettingsServiceSlots.PathRegistrationService = pathReg;
        Settings.SettingsServiceSlots.SystemService = system;
        Settings.SettingsServiceSlots.JournalService = journal;
        Settings.SettingsServiceSlots.PresetService = preset;
        Settings.SettingsServiceSlots.SmbService = smb;

        // 仪表板数据源 + 24h 趋势采样器依赖
        AppServices.SystemService = system;

        return new ComposedServices
        {
            System = system,
            Disk = disk,
            Smb = smb,
            Security = security,
            Share = share,
            User = user,
            Session = session,
            Preset = preset,
            Protocol = protocol,
            Adapters = adapters,
            Firewall = firewall,
            Diagnose = diagnose,
            Update = update,
            PathRegistration = pathReg,
            Transfer = transfer,
        };
    }
}
