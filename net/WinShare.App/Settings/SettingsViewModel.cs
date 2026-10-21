using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 「服务器配置」页（route /settings）根 VM：聚合 4 协议卡 + 能力探测 + 一键诊断 + 版本适配，
/// 提供整页刷新编排（对齐 Settings.tsx 的 load=F5 刷新面）。
/// 全部依赖走 <see cref="SettingsServiceSlots"/>，缺席槽位 → 对应卡降级。
/// </summary>
public sealed class SettingsViewModel
{
    public ProtocolDetectionViewModel Detection { get; }
    public SmbConfigCardViewModel Smb { get; }
    public NfsConfigCardViewModel Nfs { get; }
    public FtpConfigCardViewModel Ftp { get; }
    public WebdavConfigCardViewModel Webdav { get; }
    public DiagnoseViewModel Diagnose { get; }
    public OsCompatCardViewModel OsCompat { get; }

    public bool AdvancedMode { get; }

    public SettingsViewModel(bool advancedMode = true)
    {
        AdvancedMode = advancedMode;
        var proto = SettingsServiceSlots.ProtocolService;
        Detection = new ProtocolDetectionViewModel(proto);
        Smb = new SmbConfigCardViewModel(proto, advancedMode);
        Nfs = new NfsConfigCardViewModel(proto, advancedMode);
        Ftp = new FtpConfigCardViewModel(proto, advancedMode);
        Webdav = new WebdavConfigCardViewModel(proto, advancedMode);
        Diagnose = new DiagnoseViewModel(SettingsServiceSlots.DiagnoseService);
        OsCompat = new OsCompatCardViewModel(SettingsServiceSlots.SystemService);
    }

    /// <summary>整页刷新（对齐 TS load 失效重取：重探 + 各已装卡重读 + 诊断/OS 重拉）。</summary>
    public async Task RefreshAsync()
    {
        await Detection.DetectAsync();
        Smb.SetInstalled(Detection.InstalledOf(Protocol.Smb));
        Nfs.SetInstalled(Detection.InstalledOf(Protocol.Nfs));
        Ftp.SetInstalled(Detection.InstalledOf(Protocol.Ftp));
        Webdav.SetInstalled(Detection.InstalledOf(Protocol.Webdav));
        await Task.WhenAll(
            Smb.LoadAsync(), Nfs.LoadAsync(), Ftp.LoadAsync(), Webdav.LoadAsync(),
            OsCompat.LoadAsync());
    }
}

/// <summary>
/// 应用设置页 VM 族所需的组合根依赖（视图层从 AppServices + SettingsServiceSlots 组装传入）：
/// 状态仓 + 主题热切换委托（ThemeManager.Apply）+ ThemeChanged 事件发布。
/// </summary>
public sealed record SettingsPageDependencies
{
    public required AppSettingsStore Store { get; init; }
    public Action<ThemeMode>? ApplyTheme { get; init; }
    public Action? PublishThemeChanged { get; init; }
}

/// <summary>
/// 「应用设置」页（route /app-settings）根 VM：关于/检查更新/设置导入导出/
/// 使用模式/开机自启/告警规则/防火墙/账号体检/审计日志/应用日志。
/// </summary>
public sealed class AppSettingsViewModel
{
    public UsageModeCardViewModel Usage { get; }
    public AutoStartCardViewModel AutoStart { get; }
    public AlertRulesCardViewModel Alerts { get; }
    public FirewallCardViewModel Firewall { get; }
    public SecurityCardViewModel Security { get; }
    public UpdateCheckerViewModel Update { get; }
    public SettingsTransferViewModel Transfer { get; }
    public AuditLogViewModel Audit { get; }
    public AppLogViewModel AppLog { get; }

    /// <summary>应用版本号（关于卡；取自宿主程序集）。</summary>
    public string Version { get; }

    /// <summary>运行时描述（关于卡「运行时」行；.NET 桌面版替代 electron 的 Chromium/Node 双值）。</summary>
    public string RuntimeDescription { get; }

    /// <summary>项目地址三件套（对齐 src/utils/appMeta.ts 常量）。</summary>
    public string ProjectUrl { get; } = "https://github.com/Hermitweb/win-share-panel";
    public string IssuesUrl { get; } = "https://github.com/Hermitweb/win-share-panel/issues";
    public string ReleasesUrl { get; } = "https://github.com/Hermitweb/win-share-panel/releases/latest";

    public AppSettingsViewModel(
        SettingsPageDependencies deps,
        Action<string>? openExternal = null)
    {
        Usage = new UsageModeCardViewModel(
            deps.Store,
            applyTheme: deps.ApplyTheme,
            onThemeChanged: deps.PublishThemeChanged);
        AutoStart = new AutoStartCardViewModel(SettingsServiceSlots.PathRegistrationService, deps.Store);
        Alerts = new AlertRulesCardViewModel(deps.Store);
        Firewall = new FirewallCardViewModel(SettingsServiceSlots.FirewallService);
        Security = new SecurityCardViewModel(SettingsServiceSlots.SecurityService);
        var current = typeof(AppSettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
        Update = new UpdateCheckerViewModel(SettingsServiceSlots.UpdateService, current, openExternal);
        Transfer = new SettingsTransferViewModel(deps.Store);
        Audit = new AuditLogViewModel(deps.Store);
        AppLog = new AppLogViewModel();

        Version = current;
        RuntimeDescription = $"Windows .NET {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
    }

    public async Task RefreshAsync()
    {
        await AutoStart.ReadAsync();
        await Security.RunAsync();
        await Firewall.ReloadAsync();
        AppLog.Load();
        Audit.Reload();
    }
}
