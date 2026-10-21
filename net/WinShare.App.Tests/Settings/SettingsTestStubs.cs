using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 契约服务 stub（xUnit 驱动 VM；未实现成员按契约语义抛 NotSupportedException，
/// 调用方先查 capabilities——与 IProtocolAdapter 注释语义一致）。
/// </summary>
public sealed class StubProtocolService : IProtocolService
{
    public Dictionary<Protocol, StubProtocolAdapter> Adapters { get; } = new();
    public IReadOnlyList<Protocol> Registered { get; set; } = new[] { Protocol.Smb, Protocol.Nfs, Protocol.Ftp, Protocol.Webdav };
    public Dictionary<Protocol, ProtocolFeatureState>? DetectedCaps { get; set; }
    public List<Protocol> InstalledCalls { get; } = new();
    public int DetectCalls { get; private set; }

    public IProtocolAdapter GetAdapter(Protocol protocol) => Adapters[protocol];
    public IReadOnlyList<Protocol> GetRegisteredProtocols() => Registered;
    public Dictionary<Protocol, ProtocolCapabilities?> GetCapabilitiesMap()
        => Registered.ToDictionary(p => p, p => Adapters.TryGetValue(p, out var a) ? a.Capabilities : null);
    public Task<IReadOnlyList<Share>> AdapterListAsync(Protocol? protocol = null) => Task.FromResult<IReadOnlyList<Share>>(Array.Empty<Share>());
    public Task<Share> AdapterCreateAsync(CreateShareInput input) => throw new NotSupportedException();
    public Task<Share> AdapterUpdateAsync(string name, UpdateShareInput input) => throw new NotSupportedException();
    public Task AdapterDeleteAsync(Protocol protocol, string name) => throw new NotSupportedException();
    public Task AdapterToggleAsync(Protocol protocol, string name, bool enabled) => throw new NotSupportedException();
    public Task<IReadOnlyList<SharePermission>> AdapterGetPermissionsAsync(Protocol protocol, string name) => Task.FromResult<IReadOnlyList<SharePermission>>(Array.Empty<SharePermission>());
    public Task AdapterSetPermissionsAsync(Protocol protocol, string name, IReadOnlyList<SharePermission> perms) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProtocolSession>> AdapterSessionsAsync(Protocol protocol) => Task.FromResult<IReadOnlyList<ProtocolSession>>(Array.Empty<ProtocolSession>());
    public Task AdapterCloseSessionAsync(Protocol protocol, string sessionId) => throw new NotSupportedException();

    public Task<Dictionary<Protocol, ProtocolFeatureState>> DetectProtocolsAsync()
    {
        DetectCalls++;
        return Task.FromResult(DetectedCaps ?? Registered.ToDictionary(p => p, p => new ProtocolFeatureState
        {
            protocol = p,
            installed = true,
            installType = p == Protocol.Smb ? ProtocolInstallType.Builtin : ProtocolInstallType.ServerFeature,
            serviceName = "Srv",
            serviceStatus = ServiceState.Running,
            installCommand = "",
            installHint = "",
        }));
    }

    public Task InstallProtocolAsync(Protocol protocol)
    {
        InstalledCalls.Add(protocol);
        return Task.CompletedTask;
    }

    /// <summary>常用默认态：4 协议全已装 + 全能力。</summary>
    public static (StubProtocolService svc, Dictionary<Protocol, StubProtocolAdapter> adapters) BuildAllInstalled()
    {
        var svc = new StubProtocolService();
        foreach (var p in svc.Registered)
        {
            svc.Adapters[p] = new StubProtocolAdapter(p, allCapabilities: true);
        }
        return (svc, svc.Adapters);
    }
}

public sealed class StubProtocolAdapter : IProtocolAdapter
{
    public Protocol Protocol { get; }
    public ProtocolCapabilities Capabilities { get; set; }

    /// <summary>最近一次 SetConfigAsync 收到的对象（测试断言面）。</summary>
    public object? LastSetConfig { get; private set; }
    public int SetConfigCalls { get; private set; }
    public int RestartCalls { get; private set; }
    public int LoadCalls { get; private set; }

    /// <summary>LoadAsync 回给 UI 的配置（null=契约 GetConfigAsync 回 null，VM 需容错）。</summary>
    public object? ConfigSource { get; set; }
    public ServiceStatus ServiceSource { get; set; } = new() { name = "Srv", status = ServiceState.Running, startType = "Automatic" };
    public Exception? ThrowOnSet { get; set; }

    public StubProtocolAdapter(Protocol protocol, bool allCapabilities = false)
    {
        Protocol = protocol;
        Capabilities = new ProtocolCapabilities
        {
            supportsCreate = allCapabilities,
            supportsUpdate = allCapabilities,
            supportsDelete = allCapabilities,
            supportsToggle = allCapabilities,
            supportsPermissions = allCapabilities,
            supportsSessions = allCapabilities,
            supportsOpenFiles = allCapabilities,
            supportsServerConfig = true,
            supportsRestart = true,
            permissionModel = PermissionModel.SmbAcl,
        };
    }

    public Task<IReadOnlyList<Share>> ListSharesAsync() => Task.FromResult<IReadOnlyList<Share>>(Array.Empty<Share>());
    public Task<Share> CreateShareAsync(CreateShareInput input) => throw new NotSupportedException();
    public Task DeleteShareAsync(string name) => throw new NotSupportedException();
    public Task<Share> UpdateShareAsync(string name, UpdateShareInput input) => throw new NotSupportedException();
    public Task ToggleShareAsync(string name, bool enabled) => throw new NotSupportedException();
    public Task<IReadOnlyList<SharePermission>> GetPermissionsAsync(string name) => Task.FromResult<IReadOnlyList<SharePermission>>(Array.Empty<SharePermission>());
    public Task SetPermissionsAsync(string name, IReadOnlyList<SharePermission> perms) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProtocolSession>> ListSessionsAsync() => Task.FromResult<IReadOnlyList<ProtocolSession>>(Array.Empty<ProtocolSession>());
    public Task CloseSessionAsync(string sessionId) => throw new NotSupportedException();
    public Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync() => Task.FromResult<IReadOnlyList<SmbOpenFile>>(Array.Empty<SmbOpenFile>());
    public Task CloseFileAsync(string fileId) => throw new NotSupportedException();

    public Task<ServiceStatus> GetServiceStatusAsync() => Task.FromResult(ServiceSource);
    public Task RestartServiceAsync()
    {
        RestartCalls++;
        return Task.CompletedTask;
    }
    public Task<object?> GetConfigAsync()
    {
        LoadCalls++;
        return Task.FromResult<object?>(ConfigSource);
    }
    public Task SetConfigAsync(object? config)
    {
        SetConfigCalls++;
        if (ThrowOnSet is not null) throw ThrowOnSet;
        LastSetConfig = config;
        return Task.CompletedTask;
    }
    public object? DefaultConfig() => null;
    public Task<object?> RestoreDefaultAsync() => Task.FromResult<object?>(null);
}

/// <summary>防火墙 stub（组常量 + 幂等 ensure 可控）。</summary>
public sealed class StubFirewallService : IFirewallService
{
    public string FwGroup { get; } = "WinShare Panel";
    public IReadOnlyList<FirewallRule> ManagedRules { get; set; } = Array.Empty<FirewallRule>();
    public List<string> EnsuredNames { get; } = new();
    public List<string> RemovedNames { get; } = new();
    public int ListCalls { get; private set; }
    public Exception? ThrowOnEnsure { get; set; }

    public Task<IReadOnlyList<FirewallRule>> ListManagedRulesAsync()
    {
        ListCalls++;
        return Task.FromResult(ManagedRules);
    }
    public Task<IReadOnlyList<string>> EnsureRulesAsync(IReadOnlyList<DesiredRule> desired)
    {
        if (ThrowOnEnsure is not null) throw ThrowOnEnsure;
        EnsuredNames.AddRange(desired.Select(d => d.name));
        ManagedRules = [new FirewallRule { name = "r", enabled = true, ports = "445" }];
        return Task.FromResult<IReadOnlyList<string>>([new("WinShare SMB (445/TCP)")]);
    }
    public Task RemoveRuleAsync(string name)
    {
        RemovedNames.Add(name);
        return Task.CompletedTask;
    }
    public IReadOnlyList<DesiredRule> PresetRules(string kind, PresetRuleOptions? opts = null) => kind switch
    {
        "smb" => [new DesiredRule { name = "WinShare SMB (445/TCP)", ports = "445", protocol = FwProtocol.TCP }],
        "ftp" => [new DesiredRule { name = "WinShare FTP", ports = "21", protocol = FwProtocol.TCP }],
        "ftpPassive" => [new DesiredRule { name = "WinShare FTP Passive", ports = $"{opts?.passiveFrom}-{opts?.passiveTo}", protocol = FwProtocol.TCP }],
        "webdav" => [new DesiredRule { name = "WinShare WebDAV (80/TCP)", ports = "80", protocol = FwProtocol.TCP }],
        "quic" => [new DesiredRule { name = "WinShare SMB QUIC (445/UDP)", ports = "445", protocol = FwProtocol.UDP }],
        _ => throw new ArgumentException($"未知防火墙预设：{kind}"),
    };
}

public sealed class StubSecurityService : ISecurityService
{
    public SecurityReport? Report { get; set; }
    public Exception? Throw { get; set; }

    public Task<SecurityReport> GetSecurityReportAsync()
    {
        if (Throw is not null) throw Throw;
        return Task.FromResult(Report ?? new SecurityReport
        {
            Checked = 3,
            issues = [new SecurityIssue { user = "svc", level = SecurityLevel.Fail, issue = "空口令" }],
            at = 1_700_000_000_000,
        });
    }
}

public sealed class StubUpdateService : IUpdateService
{
    public string UpdateRepo { get; } = "Hermitweb/win-share-panel";
    public string ReleasesPage { get; } = "https://github.com/Hermitweb/win-share-panel/releases/latest";
    public UpdateCheckResult Result { get; set; } = new() { current = "1", latest = "2", hasUpdate = true };
    public Exception? Throw { get; set; }
    public int CheckCalls { get; private set; }

    public int CompareVersions(string a, string b) => 0;
    public Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, int timeoutMs = 8000)
    {
        CheckCalls++;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

public sealed class StubDiagnoseService : IDiagnoseService
{
    public IReadOnlyList<DiagnoseItem> Items { get; set; } =
    [
        new DiagnoseItem { key = "lanman", label = "LanmanServer 服务", status = DiagnoseStatus.Pass, detail = "运行中", fix = DiagnoseFixKind.ServiceLanman },
        new DiagnoseItem { key = "acl", label = "共享 ACL", status = DiagnoseStatus.Fail, detail = "Everyone 无读权限" },
    ];
    public string FixText { get; set; } = "已修复 ACL";

    public Task<IReadOnlyList<DiagnoseItem>> RunDiagnoseAsync(DiagnoseOptions? opts = null) => Task.FromResult(Items);
    public Task<string> ApplyFixAsync(DiagnoseFixKind fix, DiagnoseOptions? args = null) => Task.FromResult(FixText);
}

public sealed class StubPathRegistrationService : IPathRegistrationService
{
    public bool LoginShortcut { get; set; }
    public bool SetResult { get; set; }
    public int SetCalls { get; private set; }
    public Exception? ThrowOnSet { get; set; }

    public Task<string> GetUserPathAsync() => Task.FromResult("");
    public Task SetUserPathAsync(string value) => Task.CompletedTask;
    public Task<bool> BroadcastPathChangeAsync() => Task.FromResult(true);
    public Task<PathRegistration> RegisterAppInUserPathAsync() =>
        Task.FromResult(new PathRegistration { registered = true, pathLength = 10, message = "", broadcast = true });
    public Task<PathRegistration> UnregisterAppFromUserPathAsync() =>
        Task.FromResult(new PathRegistration { registered = false, pathLength = 10, message = "", broadcast = true });
    public Task<bool> IsAppInUserPathAsync() => Task.FromResult(true);

    public Task<string> SetLoginShortcutAsync(bool enabled)
    {
        SetCalls++;
        if (ThrowOnSet is not null) throw ThrowOnSet;
        LoginShortcut = SetResult;
        return Task.FromResult(enabled ? "已开启登录快捷方式" : "已关闭登录快捷方式");
    }
    public Task<bool> GetLoginShortcutAsync() => Task.FromResult(LoginShortcut);
    public Task<bool> ReadServicePendingMarkerAsync() => Task.FromResult(false);
}
