namespace WinShare.Core.Contracts;

/// <summary>
/// 防火墙规则管理（对应 electron/services/firewall.ts 导出：
/// FW_GROUP 常量 / listManagedRules / ensureRules / removeRule / presetRules）。
/// </summary>
public interface IFirewallService
{
    /// <summary>TS: FW_GROUP = 'WinShare Panel'（规则所属组，本接口内为常量）。</summary>
    string FwGroup { get; }

    /// <summary>TS: listManagedRules()（仅 WinShare Panel 组内）。</summary>
    Task<IReadOnlyList<FirewallRule>> ListManagedRulesAsync();

    /// <summary>TS: ensureRules(desired)（幂等），返回本次新建的规则名列表。</summary>
    Task<IReadOnlyList<string>> EnsureRulesAsync(IReadOnlyList<DesiredRule> desired);

    Task RemoveRuleAsync(string name);

    /// <summary>
    /// TS: presetRules(kind, opts?)（同步）；kind 取值 'smb' | 'ftp' | 'ftpPassive'（扩展见 firewall.ts switch）。
    /// </summary>
    IReadOnlyList<DesiredRule> PresetRules(string kind, PresetRuleOptions? opts = null);
}
