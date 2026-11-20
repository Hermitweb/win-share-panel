// v2.2.0 错误友好化映射表（表驱动）：规则命中 / 内部降级术语 / 兜底关键行 / Brief 短版。
using WinShare.App.Common;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Common;

public class ErrorTextTests
{
    // ===== 结构化信号优先 =====

    [Fact]
    public void PermissionCategory_MapsToElevationGuidance()
    {
        var e = Errors.NotAdmin();
        string s = ErrorText.Describe(e, "创建共享");
        Assert.Equal("创建共享失败：" + ErrorText.Rules[1].Friendly, s);
        Assert.Contains("管理员", s);
    }

    [Fact]
    public void NotAdminAction_MentionsRelaunchEntry()
    {
        // 引导必须指向可操作的入口（顶栏提示条 / 应用设置页），而不是只说「权限不足」
        string s = ErrorText.Describe(Errors.NotAdmin());
        Assert.Contains("以管理员身份重启", s);
    }

    // ===== 文本模式规则 =====

    [Theory]
    [InlineData("命令执行失败：New-SmbShare : 名称已存在", "已被占用")]
    [InlineData("系统找不到指定的路径 C:\\missing", "不存在")]
    [InlineData("命令执行超时（15000ms）", "超时")]
    [InlineData("the process cannot access the file because it is being used by another process", "占用")]
    [InlineData("服务未运行：LanmanServer stopped", "未运行")]
    [InlineData("user not found: alice", "账号不存在")]
    public void PatternRules_TranslateToActionableText(string raw, string expectContains)
    {
        string s = ErrorText.Describe(new Exception(raw));
        Assert.Contains(expectContains, s);
    }

    [Fact]
    public void DegradedJargon_MapsToRestartGuidance()
    {
        // 内部降级术语（契约服务未注册/未接入/组合根）不得直出给用户
        foreach (var raw in new[] { "共享契约服务未注册", "创建共享需要契约服务（未接入）", "组合根未接线" })
        {
            string s = ErrorText.Describe(new Exception(raw));
            Assert.Contains("重启应用", s);
        }
    }

    // ===== 兜底关键行 =====

    [Fact]
    public void Fallback_ExtractsFirstMeaningfulLine_AndAddsLogHintForTechnicalRaw()
    {
        var raw = "命令执行失败：New-SmbShare : 某某错误\r\n+ CategoryInfo: NotSpecified\r\n+ FullyQualifiedErrorId: xxx";
        string s = ErrorText.ExtractKeyLine(raw);
        Assert.StartsWith("New-SmbShare : 某某错误", s);
        Assert.EndsWith("（详情见应用日志）", s);
    }

    [Fact]
    public void Fallback_FriendlyShortMessage_NoLogNoise()
    {
        // 服务层已人话化的短文案不再追加「详情见应用日志」噪音
        string s = ErrorText.ExtractKeyLine("规则名白名单校验失败");
        Assert.Equal("规则名白名单校验失败", s);
    }

    // ===== Brief（批量失败摘要） =====

    [Fact]
    public void Brief_Permission_IsShort()
    {
        Assert.Equal("需要管理员权限", ErrorText.Brief(Errors.NotAdmin()));
    }

    [Fact]
    public void Brief_TruncatesLongRaw()
    {
        string s = ErrorText.Brief(new Exception(new string('x', 200)));
        Assert.True(s.Length <= 61);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void Describe_NullException_ReturnsUnknownFallback()
    {
        Assert.Contains("详情见应用日志", ErrorText.Describe(null, "操作"));
    }
}
