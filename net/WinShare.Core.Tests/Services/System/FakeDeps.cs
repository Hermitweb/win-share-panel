// System 服务域测试共用的假执行层与假依赖。
// 对应 TS 测试的 vi.mock 手法：
//   vi.mock('../lib/powershell')        → <see cref="FakePs"/>（命令记录 + 结果队列）
//   vi.mock('fs') 的 existsSync         → <see cref="FakeFs"/>（路径存在性注入）
//   vi.mock('./smb') / './user' / './firewall' / './protocol/registry' → 各 Fake*Service
// 只断言"命令拼对了没 / 判级对了没"，不触发任何真实 PowerShell。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Host;

namespace WinShare.Core.Tests.Services.Host;

/// <summary>TS: vi.mock('../lib/powershell') —— 记录命令串并按队列返回 JSON 结果。</summary>
internal sealed class FakePs : IPsInvoker
{
    public List<string> JsonCommands { get; } = new();
    public List<string> VoidCommands { get; } = new();

    private readonly Queue<JsonElement> _jsonResults = new();
    private readonly Queue<Exception> _jsonFailures = new();

    /// <summary>TS: mockedRunPowerShell.mockImplementation(async (cmd) => ...) 等价——按命令特征分发。</summary>
    public Func<string, JsonElement>? Dispatcher { get; set; }

    /// <summary>无分发器时的默认结果（TS mockResolvedValue 的兜底）。</summary>
    public JsonElement DefaultResult { get; set; } = JsonDocument.Parse("[]").RootElement.Clone();

    public void WhenJson(string json) => _jsonResults.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
    public void WhenJsonValue(string value) => _jsonResults.Enqueue(JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone());
    public void WhenJsonObject(object value) => _jsonResults.Enqueue(JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone());
    public void WhenJsonFails(Exception ex) => _jsonFailures.Enqueue(ex);

    public Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries)
    {
        JsonCommands.Add(command);
        if (_jsonFailures.Count > 0) return Task.FromException<JsonElement>(_jsonFailures.Dequeue());
        if (_jsonResults.Count > 0) return Task.FromResult(_jsonResults.Dequeue());
        if (Dispatcher is { } d) return Task.FromResult(d(command));
        return Task.FromResult(DefaultResult);
    }

    public Task RunVoidAsync(string command, int timeoutMs, int retries)
    {
        VoidCommands.Add(command);
        return Task.CompletedTask;
    }

    /// <summary>TS: mock.calls.filter(c => cmd.includes(x)).length</summary>
    public int CountJson(string contains) => JsonCommands.Count(c => c.Contains(contains));
}

/// <summary>TS: vi.mock('fs') 的 existsSync 注入。</summary>
internal sealed class FakeFs
{
    private readonly Dictionary<string, bool> _map = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, bool>? Predicate { get; set; }

    public void Set(string path, bool exists) => _map[path] = exists;
    public void Clear() => _map.Clear();

    public bool Exists(string path)
    {
        if (Predicate is { } p) return p(path);
        return _map.TryGetValue(path, out bool e) && e;
    }
}

/// <summary>TS: vi.mock('./smb') —— getServiceStatus / getConfig / startService。</summary>
internal sealed class FakeSmb : ISmbService
{
    public ServiceStatus Status { get; set; } = new()
    {
        name = "LanmanServer", status = ServiceState.Running, startType = "Automatic",
    };
    public SmbServerConfig Config { get; set; } = new();
    public int StartCalls { get; private set; }
    public int GetStatusCalls { get; private set; }
    public int GetConfigCalls { get; private set; }

    public Task<ServiceStatus> GetServiceStatusAsync() { GetStatusCalls++; return Task.FromResult(Status); }
    public Task<SmbServerConfig> GetConfigAsync() { GetConfigCalls++; return Task.FromResult(Config); }
    public Task StartServiceAsync() { StartCalls++; return Task.CompletedTask; }
    public Task StopServiceAsync() => Task.CompletedTask;
    public Task RestartServiceAsync() => Task.CompletedTask;
    public SmbServerConfig DefaultConfig() => new();
    public Task SetConfigAsync(SmbServerConfig config) => Task.CompletedTask;
    public Task<SmbServerConfig> RestoreDefaultAsync() => Task.FromResult(new SmbServerConfig());
    public Task<IReadOnlyList<SmbSnapshotMeta>> ListSnapshotsAsync() => Task.FromResult<IReadOnlyList<SmbSnapshotMeta>>(Array.Empty<SmbSnapshotMeta>());
    public Task RollbackSnapshotAsync(string id) => Task.CompletedTask;
}

/// <summary>TS: vi.mock('./user') —— getNtfsPermissions / getSharePermissions。</summary>
internal sealed class FakeUser : IUserService
{
    public NtfsAcl Ntfs { get; set; } = new() { path = "", entries = Array.Empty<NtfsAclEntry>() };
    public IReadOnlyList<SharePermission> SharePerms { get; set; } = Array.Empty<SharePermission>();
    public List<string> NtfsCalls { get; } = new();
    public List<string> SharePermsCalls { get; } = new();

    public Task<NtfsAcl> GetNtfsPermissionsAsync(string path) { NtfsCalls.Add(path); return Task.FromResult(Ntfs); }
    public Task<IReadOnlyList<SharePermission>> GetSharePermissionsAsync(string shareName) { SharePermsCalls.Add(shareName); return Task.FromResult(SharePerms); }
    public Task<IReadOnlyList<SharePermission>> GetUserSharePermissionsAsync(string username) => Task.FromResult<IReadOnlyList<SharePermission>>(Array.Empty<SharePermission>());
    public Task SetSharePermissionsAsync(string shareName, IReadOnlyList<SharePermission> perms) => Task.CompletedTask;
    public Task<IReadOnlyList<LocalUser>> ListUsersAsync() => Task.FromResult<IReadOnlyList<LocalUser>>(Array.Empty<LocalUser>());
    public Task<IReadOnlyList<LocalGroup>> ListGroupsAsync() => Task.FromResult<IReadOnlyList<LocalGroup>>(Array.Empty<LocalGroup>());
    public Task<LocalUser> GetUserAsync(string name) => Task.FromException<LocalUser>(new InvalidOperationException());
    public Task CreateUserAsync(CreateUserOpts opts) => Task.CompletedTask;
    public Task UpdateUserAsync(string name, UpdateUserOpts opts) => Task.CompletedTask;
    public Task DeleteUserAsync(string name) => Task.CompletedTask;
    public Task SetUserPasswordAsync(string name, string password) => Task.CompletedTask;
    public Task EnableUserAsync(string name) => Task.CompletedTask;
    public Task DisableUserAsync(string name) => Task.CompletedTask;
    public Task RenameUserAsync(string oldName, string newName) => Task.CompletedTask;
    public Task CreateGroupAsync(CreateGroupOpts opts) => Task.CompletedTask;
    public Task DeleteGroupAsync(string name) => Task.CompletedTask;
    public Task UpdateGroupAsync(string name, string description) => Task.CompletedTask;
    public Task RenameGroupAsync(string name, string newName) => Task.CompletedTask;
    public Task AddGroupMemberAsync(string group, string member) => Task.CompletedTask;
    public Task RemoveGroupMemberAsync(string group, string member) => Task.CompletedTask;
}

/// <summary>TS: vi.mock('./firewall') —— listManagedRules / ensureRules。</summary>
internal sealed class FakeFirewall : IFirewallService
{
    public IReadOnlyList<FirewallRule> Managed { get; set; } = Array.Empty<FirewallRule>();
    public IReadOnlyList<string> Created { get; set; } = Array.Empty<string>();
    public List<IReadOnlyList<DesiredRule>> EnsureCalls { get; } = new();

    public string FwGroup => FirewallService.FwGroupConst;

    public Task<IReadOnlyList<FirewallRule>> ListManagedRulesAsync() => Task.FromResult(Managed);

    public Task<IReadOnlyList<string>> EnsureRulesAsync(IReadOnlyList<DesiredRule> desired)
    {
        EnsureCalls.Add(desired);
        return Task.FromResult(Created);
    }

    public Task RemoveRuleAsync(string name) => Task.CompletedTask;
    public IReadOnlyList<DesiredRule> PresetRules(string kind, PresetRuleOptions? opts = null) => Array.Empty<DesiredRule>();
}

/// <summary>TS: vi.mock('./protocol/registry') —— adapterList / adapterSessions。</summary>
internal sealed class FakeProtocol : IProtocolService
{
    public IReadOnlyList<Share> Shares { get; set; } = Array.Empty<Share>();
    public IReadOnlyList<ProtocolSession> NfsSessions { get; set; } = Array.Empty<ProtocolSession>();
    public Exception? ListFailure { get; set; }
    public Exception? SessionsFailure { get; set; }
    public int ListCalls { get; private set; }
    public int SessionsCalls { get; private set; }

    public Task<IReadOnlyList<Share>> AdapterListAsync(WinShare.Core.Contracts.Protocol? protocol = null)
    {
        ListCalls++;
        return ListFailure is { } e ? Task.FromException<IReadOnlyList<Share>>(e) : Task.FromResult(Shares);
    }

    public Task<IReadOnlyList<ProtocolSession>> AdapterSessionsAsync(WinShare.Core.Contracts.Protocol protocol)
    {
        SessionsCalls++;
        return SessionsFailure is { } e ? Task.FromException<IReadOnlyList<ProtocolSession>>(e) : Task.FromResult(NfsSessions);
    }

    public IProtocolAdapter GetAdapter(WinShare.Core.Contracts.Protocol protocol) => throw new NotSupportedException();
    public IReadOnlyList<WinShare.Core.Contracts.Protocol> GetRegisteredProtocols() => Array.Empty<WinShare.Core.Contracts.Protocol>();
    public Dictionary<WinShare.Core.Contracts.Protocol, ProtocolCapabilities?> GetCapabilitiesMap() => new();
    public Task<Share> AdapterCreateAsync(CreateShareInput input) => throw new NotSupportedException();
    public Task<Share> AdapterUpdateAsync(string name, UpdateShareInput input) => throw new NotSupportedException();
    public Task AdapterDeleteAsync(WinShare.Core.Contracts.Protocol protocol, string name) => throw new NotSupportedException();
    public Task AdapterToggleAsync(WinShare.Core.Contracts.Protocol protocol, string name, bool enabled) => throw new NotSupportedException();
    public Task<IReadOnlyList<SharePermission>> AdapterGetPermissionsAsync(WinShare.Core.Contracts.Protocol protocol, string name) => throw new NotSupportedException();
    public Task AdapterSetPermissionsAsync(WinShare.Core.Contracts.Protocol protocol, string name, IReadOnlyList<SharePermission> perms) => throw new NotSupportedException();
    public Task AdapterCloseSessionAsync(WinShare.Core.Contracts.Protocol protocol, string sessionId) => throw new NotSupportedException();
    public Task<Dictionary<WinShare.Core.Contracts.Protocol, ProtocolFeatureState>> DetectProtocolsAsync() => throw new NotSupportedException();
    public Task InstallProtocolAsync(WinShare.Core.Contracts.Protocol protocol) => throw new NotSupportedException();
}

/// <summary>TS: 造 Share 的 helper（对齐 system.test.ts makeShare）。</summary>
internal static class ShareFixtures
{
    public static Share Make(string name, WinShare.Core.Contracts.Protocol protocol, int users = 0,
        ShareType type = ShareType.Disk) => new()
    {
        name = name,
        protocol = protocol,
        path = $"C:\\{name}",
        description = "",
        type = type,
        hidden = false,
        encrypted = false,
        concurrentUsers = users,
        status = ShareStatus.Enabled,
        cached = false,
    };
}
