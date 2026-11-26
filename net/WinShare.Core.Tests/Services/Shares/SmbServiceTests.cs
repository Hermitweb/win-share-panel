// SmbService 的读域回归测试（此前该服务**没有任何单测**——而真实缺陷恰好出在它身上）。
//
// 背景（v2.3.1 修复）：PowerShell 侧「无输出」被 PsRunner.ParseJson 归一为**空数组** []，
// 「多对象」也是数组；而 .NET 的 JsonElement.TryGetProperty 在非对象根上**抛**
// InvalidOperationException（"requires an element of type 'Object', but the target element
// has type 'Array'"）。TS 原版是 JS：`raw.X` 在数组上只会给 undefined → 落默认值，从不抛错。
// 该差异曾让「读取协议设置」「一键诊断」的 SMB 探针全线报 .NET 类型错误。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares;

namespace WinShare.Core.Tests.Services.Shares;

public class SmbServiceTests
{
    private const string ConfigJson =
        "{\"EnableSMB1Protocol\":false,\"EnableSMB2Protocol\":true,\"EnableSMB3Protocol\":true," +
        "\"EnableGuestUserAccess\":false,\"EnableInsecureGuestLogons\":false,\"AuditSmb1Access\":false," +
        "\"RequireSecuritySignature\":true,\"AnnounceServer\":true,\"MaxSessionPerConnection\":1024}";

    private static (SmbService Svc, FakePsInvoker Ps) Make()
    {
        var ps = new FakePsInvoker();
        return (new SmbService(ps), ps);
    }

    [Fact]
    /// <summary>正常路径：对象根 → 字段按原样读出（未因容错改造而失真）。</summary>
    public async Task GetConfig_ObjectRoot_ReadsFields()
    {
        var (svc, ps) = Make();
        ps.WhenJsonObject(ConfigJson);

        SmbServerConfig cfg = await svc.GetConfigAsync();

        Assert.False(cfg.enableSMB1Protocol);
        Assert.True(cfg.enableSMB2Protocol);
        Assert.True(cfg.enableSMB3Protocol);
        Assert.True(cfg.requireSecuritySignature);
        Assert.Equal(1024, cfg.maxSessionPerConnection);
    }

    [Fact]
    /// <summary>
    /// 回归：PowerShell 无输出（根为 []）时**不得**抛 .NET 的 InvalidOperationException，
    /// 而应给业务级失败（"本机未返回 SMB 服务器配置"）——静默回落默认值会让界面谎报
    /// 「SMB1 关闭」等，而实际是没读到。
    /// </summary>
    public async Task GetConfig_EmptyArrayRoot_ThrowsDomainError_NotDotNetTypeError()
    {
        var (svc, ps) = Make();
        ps.WhenJsonEmptyArray();

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.GetConfigAsync());

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("未返回 SMB 服务器配置", ex.Message);
    }

    [Fact]
    /// <summary>
    /// 回归：服务状态查询同样可能拿到 [] 根（LanmanServer 不可用/无权限）。
    /// 旧实现在这里抛 .NET 类型错误 → 一键诊断的「SMB 服务端（LanmanServer）运行」直接红字。
    /// 现在应降级为 Unknown（未知），不抛。
    /// </summary>
    public async Task GetServiceStatus_EmptyArrayRoot_DegradesToUnknown_NoThrow()
    {
        var (svc, ps) = Make();
        ps.WhenJsonEmptyArray();

        ServiceStatus st = await svc.GetServiceStatusAsync();

        Assert.Equal(ServiceState.Unknown, st.status);
    }

    [Fact]
    /// <summary>回归：JsonCompat.TryProp 在非对象根上返回 false 且**不抛**（TS: obj.X → undefined）。</summary>
    public void TryProp_NonObjectRoot_ReturnsFalseWithoutThrowing()
    {
        using JsonDocument arr = JsonDocument.Parse("[]");
        using JsonDocument obj = JsonDocument.Parse("{\"a\":1}");

        Assert.False(JsonCompat.TryProp(arr.RootElement, "a", out _));
        Assert.True(JsonCompat.TryProp(obj.RootElement, "a", out JsonElement v));
        Assert.Equal(1, v.GetInt32());
    }
}
