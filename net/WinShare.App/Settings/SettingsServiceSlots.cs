using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 设置模块契约服务槽位（T3d 新增文件；不改 AppServices 共享文件）。
/// 组合根（App.OnStartup）装配服务实现后逐槽注入；未注入的槽位保持 null，
/// 设置界面相应卡片走「服务未注册」降级（与 T3a 仪表板空态同语义：不崩、不臆造）。
/// xUnit 测试里注入 stub 即可全量驱动卡片 VM。
/// </summary>
public static class SettingsServiceSlots
{
    public static IProtocolService? ProtocolService { get; set; }
    public static IUpdateService? UpdateService { get; set; }
    public static IFirewallService? FirewallService { get; set; }
    public static ISecurityService? SecurityService { get; set; }
    public static IDiagnoseService? DiagnoseService { get; set; }
    public static IPathRegistrationService? PathRegistrationService { get; set; }
    public static ISystemService? SystemService { get; set; }

    /// <summary>测试钩子：全部槽位复位为 null（对齐契约服务缺席的出厂态）。</summary>
    public static void ResetForTesting()
    {
        ProtocolService = null;
        UpdateService = null;
        FirewallService = null;
        SecurityService = null;
        DiagnoseService = null;
        PathRegistrationService = null;
        SystemService = null;
    }
}
