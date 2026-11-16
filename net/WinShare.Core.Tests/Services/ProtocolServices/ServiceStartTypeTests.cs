// v2.1 服务启动类型写回测试（经公开面 SmbService/NfsService 驱动，命令串与白名单语义断言）。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Services.ProtocolServices;
using WinShare.Core.Services.Shares;

namespace WinShare.Core.Tests.Services.ProtocolServices;

public class ServiceStartTypeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // 白名单外 token → 拒绝且不触 PS（注入面收口）
    [Theory]
    [InlineData("Hacker")]
    [InlineData("Automatic; Remove-Item")]
    [InlineData("")]
    public async Task SetStartType_InvalidToken_RejectedBeforePs(string token)
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => new SmbService(ps).SetServiceStartTypeAsync(token));
        Assert.Equal("INVALID_PARAM", ex.Code);
        Assert.Empty(ps.VoidCalls);
    }

    // 合法 token → Set-Service 命令 + 回读校验（回读返回 Manual）
    [Fact]
    public async Task SetStartType_Valid_WritesAndVerifies()
    {
        var ps = new PsRecorder();
        var svc = new SmbService(ps);
        // 回读：Get-Service LanmanServer ... | Select Name/Status/StartType → StartType=Manual（字符串形态）
        ps.WhenJson("""{"Name":"LanmanServer","Status":4,"StartType":"Manual"}""");
        await svc.SetServiceStartTypeAsync("Manual");

        Assert.Single(ps.VoidCalls);
        Assert.Equal("Set-Service -Name 'LanmanServer' -StartupType Manual -ErrorAction Stop", ps.VoidCalls[0].Command);
        Assert.Single(ps.JsonCalls);
    }

    // 回读不一致（仍 Automatic）→ 明确报错
    [Fact]
    public async Task SetStartType_ReadBackMismatch_Throws()
    {
        var ps = new PsRecorder();
        ps.WhenJson("""{"Name":"LanmanServer","Status":4,"StartType":2}"""); // 2=Automatic（数值形态）
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => new SmbService(ps).SetServiceStartTypeAsync("Manual"));
        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("回读不一致", ex.Message, StringComparison.Ordinal);
    }

    // NFS 服务名探测失败（未装）→ 服务未装语义报错
    [Fact]
    public async Task SetStartType_NfsServiceAbsent_Throws()
    {
        var ps = new PsRecorder();
        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => new NfsService(ps).SetServiceStartTypeAsync("Automatic"));
        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("未安装或不可识别", ex.Message, StringComparison.Ordinal);
    }
}
