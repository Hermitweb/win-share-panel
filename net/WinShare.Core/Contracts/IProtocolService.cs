namespace WinShare.Core.Contracts;

/// <summary>
/// 协议路由与功能检测（对应 electron/services/protocol/registry.ts 导出：
/// getAdapter / getRegisteredProtocols / getCapabilitiesMap /
/// adapterList / adapterCreate / adapterUpdate / adapterDelete / adapterToggle /
/// adapterGetPermissions / adapterSetPermissions / adapterSessions / adapterCloseSession；
/// 以及 electron/services/protocol/detect.ts 导出：detectProtocols / installProtocol）。
/// 注：detect.ts 还有测试钩子 __resetInflightForTesting，不属于契约面。
/// </summary>
public interface IProtocolService
{
    /// <summary>TS: getAdapter(protocol)。</summary>
    IProtocolAdapter GetAdapter(Protocol protocol);

    /// <summary>TS: getRegisteredProtocols()（同步）。</summary>
    IReadOnlyList<Protocol> GetRegisteredProtocols();

    /// <summary>TS: getCapabilitiesMap()（同步）；未注册协议的 adapter 能力为 null。</summary>
    Dictionary<Protocol, ProtocolCapabilities?> GetCapabilitiesMap();

    /// <summary>TS: adapterList(protocol?)，缺省合并全部协议（并行查询）。</summary>
    Task<IReadOnlyList<Share>> AdapterListAsync(Protocol? protocol = null);

    Task<Share> AdapterCreateAsync(CreateShareInput input);

    Task<Share> AdapterUpdateAsync(string name, UpdateShareInput input);

    Task AdapterDeleteAsync(Protocol protocol, string name);

    Task AdapterToggleAsync(Protocol protocol, string name, bool enabled);

    Task<IReadOnlyList<SharePermission>> AdapterGetPermissionsAsync(Protocol protocol, string name);

    Task AdapterSetPermissionsAsync(Protocol protocol, string name, IReadOnlyList<SharePermission> perms);

    Task<IReadOnlyList<ProtocolSession>> AdapterSessionsAsync(Protocol protocol);

    Task AdapterCloseSessionAsync(Protocol protocol, string sessionId);

    /// <summary>TS: detect.ts detectProtocols()，TS Record&lt;Protocol, ProtocolFeatureState&gt;。</summary>
    Task<Dictionary<Protocol, ProtocolFeatureState>> DetectProtocolsAsync();

    /// <summary>TS: detect.ts installProtocol(protocol)。</summary>
    Task InstallProtocolAsync(Protocol protocol);
}
