// 假协议服务：适配器的「服务配置类方法」（getServiceStatus/restartService/getConfig/setConfig/
// defaultConfig/restoreDefault）必须原样委托到同名服务，绝不在这里重写命令串。
using WinShare.Core.Contracts;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

internal sealed class FakeNfsService : INfsService
{
    public int RestartCalls { get; private set; }
    public NfsServerConfig? AppliedConfig { get; private set; }
    public NfsServerConfig Default { get; } = new() { protocolVersion = "4.1", gatewayCharacterSet = "ANSI" };
    public NfsServerConfig Config { get; } = new() { protocolVersion = "3.0" };
    public NfsServerConfig Restored { get; } = new() { protocolVersion = "4.1", logActivity = true };

    public NfsServerConfig DefaultConfig() => Default;
    public Task<NfsServerConfig> GetConfigAsync() => Task.FromResult(Config);
    public Task SetConfigAsync(NfsServerConfig config) { AppliedConfig = config; return Task.CompletedTask; }
    public Task<NfsServerConfig> RestoreDefaultAsync() => Task.FromResult(Restored);
    public Task<ServiceStatus> GetServiceStatusAsync() =>
        Task.FromResult(new ServiceStatus { name = "NfsService", status = ServiceState.Running, startType = "Automatic" });
    public Task RestartServiceAsync() { RestartCalls++; return Task.CompletedTask; }
    public Task StartServiceAsync() => Task.CompletedTask;
    public Task StopServiceAsync() => Task.CompletedTask;
}

internal sealed class FakeFtpService : IFtpService
{
    public int RestartCalls { get; private set; }
    public int UnlockCalls { get; private set; }
    public FtpServerConfig? AppliedConfig { get; private set; }
    public FtpServerConfig Default { get; } = new();
    public FtpServerConfig Config { get; } = new();
    public FtpServerConfig Restored { get; } = new();

    public Task<ServiceStatus> GetServiceStatusAsync() =>
        Task.FromResult(new ServiceStatus { name = "ftpsvc", status = ServiceState.Running, startType = "Automatic" });
    public Task RestartServiceAsync() { RestartCalls++; return Task.CompletedTask; }
    public Task StartServiceAsync() => Task.CompletedTask;
    public Task StopServiceAsync() => Task.CompletedTask;
    public FtpServerConfig DefaultConfig() => Default;
    public Task EnsureFtpSectionsUnlockedAsync() { UnlockCalls++; return Task.CompletedTask; }
    public Task<FtpServerConfig> GetConfigAsync() => Task.FromResult(Config);
    public Task SetConfigAsync(FtpServerConfig config) { AppliedConfig = config; return Task.CompletedTask; }
    public Task<FtpServerConfig> RestoreDefaultAsync() => Task.FromResult(Restored);
}

internal sealed class FakeWebdavService : IWebdavService
{
    public int RestartCalls { get; private set; }
    public int UnlockCalls { get; private set; }
    public WebdavServerConfig? AppliedConfig { get; private set; }
    public WebdavServerConfig Default { get; } = new();
    public WebdavServerConfig Config { get; } = new();
    public WebdavServerConfig Restored { get; } = new();

    public Task<ServiceStatus> GetServiceStatusAsync() =>
        Task.FromResult(new ServiceStatus { name = "W3SVC", status = ServiceState.Running, startType = "Automatic" });
    public Task RestartServiceAsync() { RestartCalls++; return Task.CompletedTask; }
    public Task StartServiceAsync() => Task.CompletedTask;
    public Task StopServiceAsync() => Task.CompletedTask;
    public WebdavServerConfig DefaultConfig() => Default;
    public Task EnsureWebdavSectionsUnlockedAsync() { UnlockCalls++; return Task.CompletedTask; }
    public Task<WebdavServerConfig> GetConfigAsync() => Task.FromResult(Config);
    public Task SetConfigAsync(WebdavServerConfig config) { AppliedConfig = config; return Task.CompletedTask; }
    public Task<WebdavServerConfig> RestoreDefaultAsync() => Task.FromResult(Restored);
}
