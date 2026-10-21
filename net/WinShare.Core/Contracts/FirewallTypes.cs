namespace WinShare.Core.Contracts;

/// <summary>防火墙规则协议（TS: DesiredRule.protocol）。</summary>
public enum FwProtocol { TCP, UDP }

/// <summary>本应用创建的防火墙规则（TS: types.ts FirewallRule，仅 WinShare Panel 组内）。</summary>
public record FirewallRule
{
    public string name { get; init; }
    public bool enabled { get; init; }
    public string ports { get; init; }
}

/// <summary>期望态防火墙规则（TS: types.ts DesiredRule，ensure 幂等输入，name 同时作为幂等键）。</summary>
public record DesiredRule
{
    public string name { get; init; }
    /// <summary>单端口 "21" 或范围 "50000-51000"。</summary>
    public string ports { get; init; }
    public FwProtocol? protocol { get; init; }
}

/// <summary>presetRules 选项（TS: firewall.ts presetRules(kind, opts?)）。</summary>
public record PresetRuleOptions
{
    public int? passiveFrom { get; init; }
    public int? passiveTo { get; init; }
}
