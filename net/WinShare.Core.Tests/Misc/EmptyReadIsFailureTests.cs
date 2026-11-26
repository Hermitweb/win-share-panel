// 「读不到」≠「没有」——这三个用例固化"读到 0 条 = 读取失败 = 显式报错"的契约。
//
// 背景（v2.3.1 用户实测）：PowerShell 侧「无输出」被 ParseJson 归一为**空数组** []，
// 服务层若把它当成功返回，界面就会给出：
//   · 用户权限页一张**空白表格**（外加一句"未读取到本地组"）；
//   · 账号安全体检一句**假绿**——"已检查 0 个启用账号 / 未发现空口令 / 密码永不过期"。
// 两者都把"没读到"说成了"没有"。Windows 至少有 Administrator / SYSTEM 与若干内置组，
// 所以 0 条本身就是读取失败的信号——必须报错，而不是给空白与假绿。
using WinShare.Core.Contracts;
using WinShare.Core.Services.Host;
using WinShare.Core.Services.Users;
using WinShare.Core.Tests.Services.Shares;

namespace WinShare.Core.Tests.Misc;

public class EmptyReadIsFailureTests
{
    [Fact]
    /// <summary>本地用户读到 0 条 → 抛业务错误（不是"成功但一个用户都没有"）。</summary>
    public async Task ListUsers_EmptyResult_Throws()
    {
        var ps = new FakePsInvoker();
        ps.WhenJsonEmptyArray();
        var svc = new UserService(ps);

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.ListUsersAsync());

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("未返回本地用户", ex.Message);
    }

    [Fact]
    /// <summary>本地组读到 0 条 → 抛业务错误（同上，避免空白表与"未读取到本地组"同形）。</summary>
    public async Task ListGroups_EmptyResult_Throws()
    {
        var ps = new FakePsInvoker();
        ps.WhenJsonEmptyArray();
        var svc = new UserService(ps);

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.ListGroupsAsync());

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("未返回本地组", ex.Message);
    }

    [Fact]
    /// <summary>安全体检读到 0 个本地用户 → 抛业务错误（不得报"已检查 0 个 / 未发现问题"的假绿）。</summary>
    public async Task SecurityReport_EmptyResult_Throws()
    {
        var ps = new FakePsInvoker();
        ps.WhenJsonEmptyArray();
        var svc = new SecurityService(ps);

        AppException ex = await Assert.ThrowsAsync<AppException>(() => svc.GetSecurityReportAsync());

        Assert.Equal("COMMAND_FAILED", ex.Code);
        Assert.Contains("无法进行", ex.Message);
    }
}
