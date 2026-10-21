// 移植自 electron/services/protocol/registry.ts（adapter 注册表 + 统一路由）。
// TS 的 Partial<Record<Protocol, ProtocolAdapter>> → C# 字典；TS 可选方法（?）的
// "未实现即不支持" 语义 → 契约约定「不支持时抛 NotSupportedException」，故 registry 侧
// 的能力守卫统一以 Capabilities.supports* 位判断（与 TS `if (!adapter.updateShare)` 等价）。
// 注：命名空间用复数 Protocols —— 单数 Protocol 会与 Contracts.Protocol 枚举同名冲突。
using System.Diagnostics;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Protocols;

/// <summary>
/// IProtocolService 实现：协议路由（TS: registry.ts 全部导出）。
/// adapterList 的并行合并语义与 TS Promise.allSettled 一致：单协议失败静默跳过。
/// </summary>
public sealed class ProtocolRegistry : IProtocolService
{
    private readonly Dictionary<Protocol, IProtocolAdapter> _adapters;
    private readonly IPsInvoker _ps;

    public ProtocolRegistry(IEnumerable<IProtocolAdapter> adapters, IPsInvoker? ps = null)
    {
        _ps = ps ?? PsRunner.Default;
        _adapters = new Dictionary<Protocol, IProtocolAdapter>();
        foreach (IProtocolAdapter a in adapters)
        {
            _adapters[a.Protocol] = a;
        }
    }

    /// <summary>TS: getAdapter(protocol) —— 未注册协议抛 invalidParam。</summary>
    public IProtocolAdapter GetAdapter(Protocol protocol)
    {
        if (!_adapters.TryGetValue(protocol, out IProtocolAdapter? adapter))
        {
            throw Errors.InvalidParam($"协议 {ProtocolName(protocol)} 暂未实现");
        }
        return adapter;
    }

    // TS: `${protocol}` 模板串（小写协议名）
    private static string ProtocolName(Protocol p) => p switch
    {
        Protocol.Smb => "smb",
        Protocol.Nfs => "nfs",
        Protocol.Ftp => "ftp",
        Protocol.Webdav => "webdav",
        _ => p.ToString(),
    };

    // TS: getRegisteredProtocols() —— Object.keys(adapters)
    public IReadOnlyList<Protocol> GetRegisteredProtocols() => _adapters.Keys.ToList();

    // TS: getCapabilitiesMap() —— 固定四协议顺序，未注册为 null
    public Dictionary<Protocol, ProtocolCapabilities?> GetCapabilitiesMap()
    {
        var result = new Dictionary<Protocol, ProtocolCapabilities?>();
        foreach (Protocol p in new[] { Protocol.Smb, Protocol.Nfs, Protocol.Ftp, Protocol.Webdav })
        {
            result[p] = _adapters.TryGetValue(p, out IProtocolAdapter? a) ? a.Capabilities : null;
        }
        return result;
    }

    // TS: adapterList(protocol?) —— 协议未装/查询失败时优雅返回空数组，不向 UI 抛错
    public async Task<IReadOnlyList<Share>> AdapterListAsync(Protocol? protocol = null)
    {
        // M-8：按次时间戳计时，规避并发调用下同名标签冲突
        var sw = Stopwatch.StartNew();
        if (protocol is not null)
        {
            try
            {
                IReadOnlyList<Share> result = await GetAdapter(protocol.Value).ListSharesAsync();
                LogDone(protocol, sw.ElapsedMilliseconds, result.Count);
                return result;
            }
            catch
            {
                LogDone(protocol, sw.ElapsedMilliseconds, 0);
                return Array.Empty<Share>();
            }
        }
        // 合并所有协议：并行查询，避免 4 个 PowerShell 进程串行启动造成 2-4s 延迟
        IReadOnlyList<Protocol> protos = GetRegisteredProtocols();
        Logger.Info("perf", $"adapterList 并行查询 {protos.Count} 个协议: {string.Join(", ", protos.Select(ProtocolName))}");
        // TS: Promise.all(protos.map(p => getAdapter(p).listShares())) —— 先全部启动再逐个 await
        var tasks = protos.Select(p => GetAdapter(p).ListSharesAsync()).ToList();
        var all = new List<Share>();
        foreach (Task<IReadOnlyList<Share>> t in tasks)
        {
            try
            {
                all.AddRange(await t);
            }
            catch
            {
                // 单协议失败静默跳过（TS: allSettled fulfilled 分支）
            }
        }
        LogDone(null, sw.ElapsedMilliseconds, all.Count);
        return all;
    }

    private static void LogDone(Protocol? protocol, long ms, int n) =>
        Logger.Info("perf", $"adapterList({(protocol is null ? "all" : ProtocolName(protocol.Value))}) 耗时 {ms}ms，返回 {n} 条");

    public async Task<Share> AdapterCreateAsync(CreateShareInput input) =>
        await GetAdapter(input.protocol).CreateShareAsync(input);

    public async Task<Share> AdapterUpdateAsync(string name, UpdateShareInput input)
    {
        IProtocolAdapter adapter = GetAdapter(input.protocol);
        // TS: if (!adapter.updateShare) throw Errors.invalidParam(`${input.protocol} 协议不支持更新`)
        if (!adapter.Capabilities.supportsUpdate)
        {
            throw Errors.InvalidParam($"{ProtocolName(input.protocol)} 协议不支持更新");
        }
        return await adapter.UpdateShareAsync(name, input);
    }

    public async Task AdapterDeleteAsync(Protocol protocol, string name) =>
        await GetAdapter(protocol).DeleteShareAsync(name);

    public async Task AdapterToggleAsync(Protocol protocol, string name, bool enabled)
    {
        IProtocolAdapter adapter = GetAdapter(protocol);
        if (!adapter.Capabilities.supportsToggle)
        {
            throw Errors.InvalidParam($"{ProtocolName(protocol)} 协议不支持启停");
        }
        await adapter.ToggleShareAsync(name, enabled);
    }

    public async Task<IReadOnlyList<SharePermission>> AdapterGetPermissionsAsync(Protocol protocol, string name)
    {
        IProtocolAdapter adapter = GetAdapter(protocol);
        if (!adapter.Capabilities.supportsPermissions)
        {
            throw Errors.InvalidParam($"{ProtocolName(protocol)} 协议不支持权限管理");
        }
        try
        {
            return await adapter.GetPermissionsAsync(name);
        }
        catch
        {
            // 协议未装或查询失败：返回空，避免打开权限抽屉时弹错误
            return Array.Empty<SharePermission>();
        }
    }

    public async Task AdapterSetPermissionsAsync(Protocol protocol, string name, IReadOnlyList<SharePermission> perms)
    {
        IProtocolAdapter adapter = GetAdapter(protocol);
        if (!adapter.Capabilities.supportsPermissions)
        {
            throw Errors.InvalidParam($"{ProtocolName(protocol)} 协议不支持权限管理");
        }
        await adapter.SetPermissionsAsync(name, perms);
    }

    public async Task<IReadOnlyList<ProtocolSession>> AdapterSessionsAsync(Protocol protocol)
    {
        IProtocolAdapter adapter = GetAdapter(protocol);
        if (!adapter.Capabilities.supportsSessions)
        {
            throw Errors.InvalidParam($"{ProtocolName(protocol)} 协议不支持会话监控");
        }
        try
        {
            return await adapter.ListSessionsAsync();
        }
        catch
        {
            // 协议未装或查询失败：返回空，避免切到会话 Tab 时弹错误
            return Array.Empty<ProtocolSession>();
        }
    }

    public async Task AdapterCloseSessionAsync(Protocol protocol, string sessionId)
    {
        IProtocolAdapter adapter = GetAdapter(protocol);
        if (!adapter.Capabilities.supportsSessions)
        {
            throw Errors.InvalidParam($"{ProtocolName(protocol)} 协议不支持关闭会话");
        }
        await adapter.CloseSessionAsync(sessionId);
    }

    // detect.ts 域（协议安装检测与安装）
    public Task<Dictionary<Protocol, ProtocolFeatureState>> DetectProtocolsAsync() =>
        ProtocolDetector.DetectProtocolsAsync(_ps);

    public Task InstallProtocolAsync(Protocol protocol) =>
        ProtocolDetector.InstallProtocolAsync(_ps, protocol);
}
