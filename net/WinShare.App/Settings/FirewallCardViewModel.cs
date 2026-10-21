using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>防火墙预设条目（kind/label/note 逐条对齐 TS FW_PRESETS [Source: AppSettingsCards.tsx L315-330]）。</summary>
public sealed record FirewallPresetDef(string Kind, string Label, string Note);

/// <summary>
/// 防火墙规则卡（只管理「WinShare Panel」组内规则：列表/新增/删除均不碰系统规则）。
/// 新增流程对齐 TS：先 PresetRules(kind,opts) 取期望规则（名称/端口/协议由服务端定义，前端不自造）
/// → 幂等 EnsureRules → 按新建条数出文案；失败重载列表（可能部分写入，以读回为准）。
/// </summary>
public sealed class FirewallCardViewModel : SettingsCardBase
{
    readonly IFirewallService? _svc;

    public static IReadOnlyList<FirewallPresetDef> Presets { get; } =
    [
        new("smb", "SMB 文件共享（445/TCP）", "客户端访问 \\\\计算机名\\共享 必需"),
        new("ftp", "FTP 控制 + 默认被动段（21、50000-51000）", "IIS FTP 站点常用组合"),
        new("ftpPassive", "FTP 被动端口范围（自定义）", "应与 FTP 配置里的防火墙范围一致"),
        new("webdav", "WebDAV（80/TCP）", "HTTP；若启用 HTTPS 需另行放行 443"),
        new("quic", "SMB QUIC（445/UDP）", "需 Server 2022 / Win11 23H2+"),
    ];

    public FirewallCardViewModel(IFirewallService? service)
    {
        _svc = service;
        SelectedKind = "smb";
        PassiveFrom = 50000;
        PassiveTo = 51000;
        Raise();
    }

    public string SelectedKind { get; private set; }
    public int? PassiveFrom { get; private set; }
    public int? PassiveTo { get; private set; }
    public IReadOnlyList<FirewallRule> Rules { get; private set; } = Array.Empty<FirewallRule>();
    public string? Removing { get; private set; }

    public FirewallPresetDef SelectedPreset =>
        Presets.FirstOrDefault(p => p.Kind == SelectedKind) ?? Presets[0];

    public bool NeedRange => SelectedKind == "ftpPassive";

    /// <summary>被动端口范围合法（对齐 TS rangeOk：双非空 + [1,65535] + 起≤止）。</summary>
    public bool RangeOk =>
        PassiveFrom is { } f && PassiveTo is { } t &&
        f >= 1 && t <= 65535 && f <= t;

    public void SelectPreset(string kind) { SelectedKind = kind; Raise(); }
    public void SetPassiveRange(int? from, int? to)
    {
        PassiveFrom = from;
        PassiveTo = to;
        Raise();
    }

    public Task ReloadAsync()
    {
        if (_svc is null)
        {
            Error = "防火墙服务未注册（等待 Core 服务层实现）：组内规则不可读";
            return Task.CompletedTask;
        }
        Busy = true;
        return Task.Run(async () =>
        {
            try
            {
                Rules = await _svc.ListManagedRulesAsync();
                Error = null;
            }
            catch (Exception e)
            {
                Error = $"读取组内规则失败：{e.Message}";
            }
            finally
            {
                Busy = false;
            }
        });
    }

    /// <summary>添加当前预设（对齐 TS add；成功返回 null，失败返回原因）。</summary>
    public Task<string?> AddAsync()
    {
        if (_svc is null) return Task.FromResult<string?>("防火墙服务未注册，无法添加规则");
        if (Busy) return Task.FromResult<string?>(null);
        if (NeedRange && !RangeOk) return Task.FromResult<string?>(null);
        Busy = true;
        return Task.Run(async () =>
        {
            try
            {
                var opts = NeedRange
                    ? new PresetRuleOptions { passiveFrom = PassiveFrom ?? 0, passiveTo = PassiveTo ?? 0 }
                    : null;
                var desired = _svc.PresetRules(SelectedKind, opts);
                var created = await _svc.EnsureRulesAsync(desired);
                Rules = await _svc.ListManagedRulesAsync();
                if (created.Count > 0)
                {
                    Success = $"已添加 {created.Count} 条组内规则：{string.Join("、", created)}";
                }
                else
                {
                    Success = "该规则已在「WinShare Panel」组内，无需重复添加";
                }
                return null;
            }
            catch (Exception e)
            {
                Error = $"添加防火墙规则失败：{e.Message}";
                await ReloadAsync();
                return e.Message;
            }
            finally
            {
                Busy = false;
            }
        });
    }

    /// <summary>删除组内一条规则（对齐 TS remove；只影响 WinShare Panel 组）。</summary>
    public Task<string?> RemoveAsync(string name)
    {
        if (_svc is null) return Task.FromResult<string?>("防火墙服务未注册，无法删除规则");
        if (Removing is not null) return Task.FromResult<string?>(null);
        Removing = name;
        Raise();
        return Task.Run(async () =>
        {
            try
            {
                await _svc.RemoveRuleAsync(name);
                Success = $"已删除组内规则「{name}」";
                Rules = await _svc.ListManagedRulesAsync();
                return null;
            }
            catch (Exception e)
            {
                Error = $"删除防火墙规则失败：{e.Message}";
                await ReloadAsync();
                return e.Message;
            }
            finally
            {
                Removing = null;
                Raise(nameof(Removing));
            }
        });
    }
}
