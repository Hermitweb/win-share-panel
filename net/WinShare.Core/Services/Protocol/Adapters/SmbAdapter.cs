// 移植自 electron/services/protocol/adapters/smbAdapter.ts（SMB 协议适配器）。
// TS 该文件是「纯委托 + 日志」包装：转调 share.* / session.* / smb.* / user.*；
// C# 侧同样委托给已落地的 ShareService / SessionService / SmbService / UserService，
// 不重写任何 PS 命令串（命令串真值在那些服务里，已与 TS 逐字对齐）。
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Protocols.Adapters;

/// <summary>SMB 协议适配器（TS: smbAdapter.ts smbAdapter）。</summary>
public sealed class SmbAdapter : IProtocolAdapter
{
    private readonly IShareService _share;
    private readonly ISessionService _session;
    private readonly ISmbService _smb;
    private readonly IUserService _user;

    public SmbAdapter(IShareService share, ISessionService session, ISmbService smb, IUserService user)
    {
        _share = share;
        _session = session;
        _smb = smb;
        _user = user;
    }

    public Protocol Protocol => Protocol.Smb;

    // TS: capabilities —— 全能力（SMB 是唯一全功能协议）
    public ProtocolCapabilities Capabilities { get; } = new()
    {
        supportsCreate = true,
        supportsUpdate = true,
        supportsDelete = true,
        supportsToggle = true,
        supportsPermissions = true,
        supportsSessions = true,
        supportsOpenFiles = true,
        supportsServerConfig = true,
        supportsRestart = true,
        permissionModel = PermissionModel.SmbAcl,
    };

    public Task<IReadOnlyList<Share>> ListSharesAsync() => _share.ListSharesAsync();

    public async Task<Share> CreateShareAsync(CreateShareInput input)
    {
        Logger.Info("adapter:smbAdapter", $"[createShare:smb] 适配器接收输入: {input.name}");
        CreateShareOpts opts = new()
        {
            name = input.name,
            path = input.path,
            description = input.description,
            fullAccess = input.fullAccess,
            changeAccess = input.changeAccess,
            readAccess = input.readAccess,
            noAccess = input.noAccess,
            encrypted = input.encrypted,
            encryptData = input.encryptData,
            concurrentUserLimit = input.concurrentUserLimit,
            cachingMode = input.cachingMode,
            folderEnumerationMode = input.folderEnumerationMode,
            shareShadowCopy = input.shareShadowCopy,
        };
        try
        {
            Share result = await _share.CreateShareAsync(opts);
            Logger.Info("adapter:smbAdapter", $"[createShare:smb] 共享创建完成: {input.name}");
            return result;
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[createShare:smb] 创建失败: {input.name} {e.Message}");
            throw;
        }
    }

    public async Task DeleteShareAsync(string name)
    {
        Logger.Info("adapter:smbAdapter", $"[deleteShare:smb] 删除共享: {name}");
        try
        {
            await _share.DeleteShareAsync(name);
            Logger.Info("adapter:smbAdapter", $"[deleteShare:smb] 删除成功: {name}");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[deleteShare:smb] 删除失败: {name} {e.Message}");
            throw;
        }
    }

    public async Task<Share> UpdateShareAsync(string name, UpdateShareInput input)
    {
        Logger.Info("adapter:smbAdapter", $"[updateShare:smb] 更新共享: {name}");
        try
        {
            Share result = await _share.UpdateShareAsync(name, new UpdateShareOpts { description = input.description });
            Logger.Info("adapter:smbAdapter", $"[updateShare:smb] 更新成功: {name}");
            return result;
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[updateShare:smb] 更新失败: {name} {e.Message}");
            throw;
        }
    }

    public async Task ToggleShareAsync(string name, bool enabled)
    {
        Logger.Info("adapter:smbAdapter", $"[toggleShare:smb] 切换共享状态: {name} → {(enabled ? "启用" : "禁用")}");
        try
        {
            await _share.ToggleShareAsync(name, enabled);
            Logger.Info("adapter:smbAdapter", $"[toggleShare:smb] 切换成功: {name}");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[toggleShare:smb] 切换失败: {name} {e.Message}");
            throw;
        }
    }

    public Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name) =>
        _share.GetSharePermissionsAsync(name);

    public async Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms)
    {
        Logger.Info("adapter:smbAdapter", $"[setPermissions:smb] 设置权限: {name}（{perms.Count} 条）");
        try
        {
            await _user.SetSharePermissionsAsync(name, perms);
            Logger.Info("adapter:smbAdapter", $"[setPermissions:smb] 权限设置成功: {name}");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[setPermissions:smb] 权限设置失败: {name} {e.Message}");
            throw;
        }
    }

    // TS: listSessions() —— 把 SmbSession 映射成通用 ProtocolSession（sessionId = clientId）
    public async Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync()
    {
        IReadOnlyList<SmbSession> sessions = await _session.ListSessionsAsync();
        return sessions.Select(s => new ProtocolSession
        {
            protocol = Protocol.Smb,
            sessionId = s.clientId,
            clientUserName = s.clientUserName,
            clientComputerName = s.clientComputerName,
            sessionStartTime = s.sessionStartTime,
            clientOpenFiles = s.clientOpenFiles,
            clientIdleTime = s.clientIdleTime,
            bytesReceived = s.bytesReceived,
            bytesSent = s.bytesSent,
        }).ToList();
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        // SMB 的 sessionId 形如 clientUserName@computerName；closeSession 接收 clientUserName。
        // 取最后一个 '@' 前部分（兼容 UPN：user@domain.com）——与 TS 逐字一致。
        int idx = sessionId.LastIndexOf('@');
        string clientUserName = idx >= 0 ? sessionId[..idx] : sessionId;
        Logger.Info("adapter:smbAdapter", $"[closeSession:smb] 关闭会话: {sessionId} → clientUserName: {clientUserName}");
        try
        {
            await _session.CloseSessionAsync(clientUserName);
            Logger.Info("adapter:smbAdapter", $"[closeSession:smb] 会话关闭成功: {sessionId}");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[closeSession:smb] 会话关闭失败: {sessionId} {e.Message}");
            throw;
        }
    }

    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() => _session.ListOpenFilesAsync();

    public async Task CloseFileAsync(string fileId)
    {
        Logger.Info("adapter:smbAdapter", $"[closeFile:smb] 关闭文件: {fileId}");
        try
        {
            await _session.CloseFileAsync(fileId);
            Logger.Info("adapter:smbAdapter", $"[closeFile:smb] 文件关闭成功: {fileId}");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[closeFile:smb] 文件关闭失败: {fileId} {e.Message}");
            throw;
        }
    }

    public Task<ServiceStatus> GetServiceStatusAsync() => _smb.GetServiceStatusAsync();

    public async Task RestartServiceAsync()
    {
        Logger.Info("adapter:smbAdapter", "[restartService:smb] 重启 SMB 服务...");
        try
        {
            await _smb.RestartServiceAsync();
            Logger.Info("adapter:smbAdapter", "[restartService:smb] 服务重启成功");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[restartService:smb] 服务重启失败: {e.Message}");
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        Logger.Info("adapter:smbAdapter", "[stopService:smb] 停止 SMB 服务...");
        try
        {
            await _smb.StopServiceAsync();
            Logger.Info("adapter:smbAdapter", "[stopService:smb] 服务已停止");
        }
        catch (Exception e)
        {
            Logger.Error("adapter:smbAdapter", $"[stopService:smb] 停止失败: {e.Message}");
            throw;
        }
    }

    // TS: getConfig()/setConfig()/defaultConfig()/restoreDefault() —— 同步转调 smb.*
    public async Task<object?> GetConfigAsync() => await _smb.GetConfigAsync();

    public async Task SetConfigAsync(object? config)
    {
        Logger.Info("adapter:smbAdapter", "[setConfig:smb] 设置服务器配置");
        if (config is not SmbServerConfig typed)
        {
            throw Errors.InvalidParam("SMB 配置对象类型不匹配");
        }
        await _smb.SetConfigAsync(typed);
    }

    public object? DefaultConfig() => new SmbServerConfig();

    public async Task<object?> RestoreDefaultAsync() => await _smb.RestoreDefaultAsync();
}
