using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 账号体检 / 检查更新 / 一键诊断卡测试。
/// [Source: AppSettingsCards.tsx SecurityCard（ISSUE_LEVEL 映射 + 全清文案）
///  + UpdateChecker.tsx（失败不得显示成「已是最新」）+ electron/services/diagnose.ts 契约语义]
/// </summary>
public class SecurityUpdateDiagnoseVmTests
{
    // ===== 账号安全体检 =====

    [Fact]
    public async Task 体检_级别映射对齐TS_ISSUE_LEVEL()
    {
        var (label, color) = SecurityCardViewModel.LevelInfo(SecurityLevel.Fail);
        Assert.Equal("严重", label);
        Assert.Equal("red", color);
        var (label2, color2) = SecurityCardViewModel.LevelInfo(SecurityLevel.Warn);
        Assert.Equal("提醒", label2);
        Assert.Equal("orange", color2);
    }

    [Fact]
    public async Task 体检_无问题_全清文案()
    {
        var svc = new StubSecurityService
        {
            Report = new SecurityReport { Checked = 5, issues = [], at = 1_700_000_000_000 },
        };
        var vm = new SecurityCardViewModel(svc);
        await vm.RunAsync();
        Assert.NotNull(vm.NoIssuesText);
        Assert.Contains("共检查 5 个启用账号", vm.NoIssuesText);
        Assert.False(vm.HasIssues);
    }

    [Fact]
    public async Task 体检_失败_错误面文案()
    {
        var svc = new StubSecurityService { Throw = new UnauthorizedAccessException("需要管理员") };
        var vm = new SecurityCardViewModel(svc);
        await vm.RunAsync();
        Assert.Equal("体检失败：需要管理员", vm.ReportError);
    }

    [Fact]
    public void 体检_服务缺席_构造即降级()
    {
        var vm = new SecurityCardViewModel(null);
        Assert.NotNull(vm.Error);
    }

    // ===== 检查更新 =====

    [Fact]
    public async Task 检查更新_失败态不得当成已是最新()
    {
        var svc = new StubUpdateService
        {
            Result = new UpdateCheckResult
            {
                current = "1.4.2", latest = "", hasUpdate = false,
                unavailable = true, reason = "GitHub API 不可达（网络受限）",
            },
        };
        var vm = new UpdateCheckerViewModel(svc, "1.4.2");
        await vm.CheckAsync();
        // 关键语义：unavailable → StatusText 必须是失败文案，不是「已是最新」
        Assert.Contains("检查更新失败", vm.StatusText);
        Assert.DoesNotContain("已是最新", vm.StatusText);
    }

    [Fact]
    public async Task 检查更新_有新版本_文案()
    {
        var svc = new StubUpdateService
        {
            Result = new UpdateCheckResult
            {
                current = "1.4.2", latest = "1.5.0", hasUpdate = true,
                releaseUrl = "https://github.com/x/releases/tag/1.5.0",
            },
        };
        var vm = new UpdateCheckerViewModel(svc, "1.4.2");
        await vm.CheckAsync();
        Assert.Equal("发现新版本 1.5.0", vm.StatusText);
    }

    [Fact]
    public async Task 检查更新_通道异常_落卡片错误面()
    {
        var svc = new StubUpdateService { Throw = new InvalidOperationException("通道不存在") };
        var vm = new UpdateCheckerViewModel(svc, "1.4.2");
        await vm.CheckAsync();
        Assert.Equal("通道不存在", vm.ErrorText);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task 打开下载页_优先结果releaseUrl_回退服务发布页()
    {
        var opened = new List<string>();
        var svc = new StubUpdateService
        {
            Result = new UpdateCheckResult { current = "1", latest = "1", hasUpdate = false, releaseUrl = "" },
        };
        var vm = new UpdateCheckerViewModel(svc, "1", openExternal: u => opened.Add(u));
        await vm.CheckAsync();
        await vm.OpenReleaseAsync();
        Assert.Equal(svc.ReleasesPage, opened[0]); // 空 releaseUrl → 回退
    }

    // ===== 一键诊断 =====

    [Fact]
    public async Task 诊断_运行后_状态文案映射()
    {
        var vm = new DiagnoseViewModel(new StubDiagnoseService());
        await vm.RunAsync(null);
        var items = vm.Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(("通过", "green"), DiagnoseViewModel.StatusInfo(items[0].status));
        Assert.Equal(("失败", "red"), DiagnoseViewModel.StatusInfo(items[1].status));
    }

    [Fact]
    public async Task 诊断_修复按钮语义_返回说明文本()
    {
        var vm = new DiagnoseViewModel(new StubDiagnoseService());
        var text = await vm.ApplyFixAsync(DiagnoseFixKind.ServiceLanman, null);
        Assert.Equal("已修复 ACL", text ?? "");
    }

    [Fact]
    public void 诊断_服务缺席_入口降级说明()
    {
        var vm = new DiagnoseViewModel(null);
        Assert.False(vm.Available);
        Assert.NotNull(vm.DegradedNote);
    }
}
