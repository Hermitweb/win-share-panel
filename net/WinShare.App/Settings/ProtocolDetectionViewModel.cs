using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>横幅徽章（label + 展示色；色名与 TS antd Tag 色板对齐，视图层映射 WPF 色）。</summary>
public sealed record CapabilityBadge(string Label, string Color);

/// <summary>
/// 协议能力探测 + 未安装引导（G5：协议已装/未装判定 [Source:
/// src/components/ProtocolCapabilityBanner.tsx + src/hooks/useEnsureProtocolCaps.ts]）。
/// 判定数据源 = IProtocolService.DetectProtocolsAsync 的 capabilities；
/// 服务缺席（slot 为 null）时整段降级：InstalledStates 为空 + DegradedNote 说明原因，
/// 面板不展示「已装/未装」断言，不臆造。
/// </summary>
public sealed class ProtocolDetectionViewModel : SettingsCardBase
{
    readonly IProtocolService? _svc;
    readonly object _gate = new();
    Dictionary<Protocol, ProtocolFeatureState>? _caps;

    /// <summary>是否已完成过一轮探测（成功或失败）；false=未知态（null 语义）。</summary>
    public bool Detected { get; private set; }

    /// <summary>服务缺席（slot 未注入）→ 视图整段降级占位（G5 处理路径）。</summary>
    public bool ServiceMissing => _svc is null;

    public string? DegradedNote { get; private set; }

    public IReadOnlyCollection<CapabilityBadge> Badges { get; private set; } = Array.Empty<CapabilityBadge>();

    public ProtocolDetectionViewModel(IProtocolService? service)
    {
        _svc = service;
        if (service is null)
        {
            DegradedNote = "协议探测服务未注册（等待 Core 服务层实现）：各协议安装状态暂无法判定，配置面板以只读降级呈现。";
        }
    }

    /// <summary>已装/未装（true=已装 false=未装 null=未知）；对齐 TS <c>installed</c> 三态。</summary>
    public bool? InstalledOf(Protocol protocol)
    {
        lock (_gate)
        {
            if (_caps is null) return null;
            return _caps.TryGetValue(protocol, out var cap) ? cap.installed : null;
        }
    }

    /// <summary>探测特征态（视图渲染徽章/提示用）。</summary>
    public ProtocolFeatureState? FeatureStateOf(Protocol protocol)
    {
        lock (_gate)
        {
            if (_caps is null) return null;
            return _caps.TryGetValue(protocol, out var cap) ? cap : null;
        }
    }

    /// <summary>未安装协议清单（对齐 TS banner 的 uninstalled 列表；含 SMB 内置项，SMB 恒 installed）。</summary>
    public IReadOnlyList<Protocol> UninstalledProtocols()
    {
        var list = new List<Protocol>();
        foreach (var p in Enum.GetValues<Protocol>())
        {
            var st = InstalledOf(p);
            if (st is false) list.Add(p);
        }
        return list;
    }

    /// <summary>安装命令存在性（视图据此显隐「安装」按钮；对齐 TS <c>cap.installCommand</c> 判定）。</summary>
    public bool HasInstallCommand(Protocol protocol)
        => FeatureStateOf(protocol)?.installCommand is { Length: > 0 };

    /// <summary>探测（对齐 useEnsureProtocolCaps 挂载探测；失败记降级说明不崩面板）。</summary>
    public async Task DetectAsync()
    {
        if (_svc is null) return;
        Busy = true;
        try
        {
            var caps = await _svc.DetectProtocolsAsync();
            lock (_gate) { _caps = caps; }
            Detected = true;
            RaiseBadges();
            DegradedNote = null;
        }
        catch (Exception e)
        {
            DegradedNote = $"协议探测失败：{e.Message}（面板按未装降级渲染）";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 安装（对齐 TS handleInstall：安装 → 成功文案「安装成功，可能需要重启系统」→ 重探刷新缓存）。
    /// 返回 null=成功文案，否则=失败文案。
    /// </summary>
    public async Task<string?> InstallAsync(Protocol protocol)
    {
        if (_svc is null) return "协议服务未注册，无法安装";
        if (Busy) return null;
        Busy = true;
        try
        {
            await _svc.InstallProtocolAsync(protocol);
            Success = "安装成功，可能需要重启系统";
            await DetectAsync();
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    void RaiseBadges()
    {
        lock (_gate)
        {
            if (_caps is null)
            {
                Badges = Array.Empty<CapabilityBadge>();
                Raise(nameof(Badges));
                return;
            }
            Badges = _caps.Values
                .Where(c => !c.installed || c.serviceStatus == ServiceState.Stopped)
                .SelectMany(c => InferBadges(c))
                .ToList();
        }
        Raise(nameof(Badges));
    }

    /// <summary>
    /// 徽章推断（纯函数，逐条对齐 TS renderStatusBadges 正则 [Source:
    /// src/components/ProtocolCapabilityBanner.tsx L103-135]）。
    /// 已安装且服务在跑 → 无徽章；iis-role 类协议按 installHint 文本推 IIS/角色细分态。
    /// </summary>
    public static IReadOnlyList<CapabilityBadge> InferBadges(ProtocolFeatureState cap)
    {
        if (cap.installed && cap.serviceStatus != ServiceState.Stopped) return Array.Empty<CapabilityBadge>();
        var badges = new List<CapabilityBadge>();
        if (cap.installType == ProtocolInstallType.IisRole)
        {
            var hint = cap.installHint ?? "";
            if (Regex.IsMatch(hint, "IIS.*已安装|IIS 基础.*已安装"))
            {
                badges.Add(new CapabilityBadge("IIS 已装", "green"));
                badges.Add(new CapabilityBadge($"{cap.protocol.Display()} 角色未装", "orange"));
            }
            else if (Regex.IsMatch(hint, "IIS.*未安装|IIS.*均未安装"))
            {
                badges.Add(new CapabilityBadge("IIS 未装", "red"));
            }
            if (Regex.IsMatch(hint, "服务未运行|ftpsvc.*未运行|W3SVC.*未运行"))
            {
                badges.Add(new CapabilityBadge("服务已停", "orange"));
            }
        }
        if (cap.serviceStatus == ServiceState.Stopped && cap.installed)
        {
            badges.Add(new CapabilityBadge("服务已停", "orange"));
        }
        return badges.Distinct().ToList();
    }
}
