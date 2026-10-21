namespace WinShare.Core.Contracts;

/// <summary>
/// 协议适配器（对应 electron/services/protocol/ProtocolAdapter.ts ProtocolAdapter 接口；
/// 各实现见 electron/services/protocol/adapters/{smb,nfs,ftp,webdav}Adapter.ts。
/// TS 中可选方法（?）由实现不实现或抛 Errors.unsupported；C# 侧统一声明全部成员，
/// 不支持的能力实现体必须抛 NotSupportedException（与 TS 语义一致），
/// 调用方必须先查 Capabilities（supports* 位）再调用）。
/// </summary>
public interface IProtocolAdapter
{
    Protocol Protocol { get; }
    ProtocolCapabilities Capabilities { get; }

    // 必选：列表/创建/删除
    Task<IReadOnlyList<Share>> ListSharesAsync();
    Task<Share> CreateShareAsync(CreateShareInput input);
    Task DeleteShareAsync(string name);

    // 可选：按能力实现（不支持时抛 NotSupportedException）
    Task<Share> UpdateShareAsync(string name, UpdateShareInput input);
    Task ToggleShareAsync(string name, bool enabled);
    Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name);
    Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms);
    Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync();
    Task CloseSessionAsync(string sessionId);
    Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync();
    Task CloseFileAsync(string fileId);

    // 协议级配置与服务控制（TS: unknown 类型 → C# object?）
    Task<ServiceStatus> GetServiceStatusAsync();
    Task RestartServiceAsync();
    /// <summary>TS: 原版无此成员（UI「停止」按钮当时被禁用）；补齐服务层能力后启用。</summary>
    Task StopServiceAsync();
    Task<object?> GetConfigAsync();
    Task SetConfigAsync(object? config);
    /// <summary>TS: defaultConfig?()（同步默认配置对象，供 UI 表单初始化/重置预填）。</summary>
    object? DefaultConfig();
    Task<object?> RestoreDefaultAsync();
}
