using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 权限矩阵纯逻辑测试（对齐 src/components/PermissionMatrix.tsx 的模块级纯函数：
/// pickAccess 最严格优先 / 宽权限账号 / HTML 转义与自包含报告 / CSV+JSON 导出）。
/// </summary>
public class PermissionMatrixLogicTests
{
    static SharePermission Perm(string account, ShareAccess access, bool deny = false) => new()
    {
        shareName = "Docs", account = account, accountType = AccountType.User, access = access, deny = deny,
    };

    // ===== pickAccess（Deny > Full > Change > Read）=====

    [Fact]
    public void PickAccess_EmptyReturnsDash()
    {
        Assert.Equal("-", PermissionMatrixLogic.PickAccess(null));
        Assert.Equal("-", PermissionMatrixLogic.PickAccess(Array.Empty<SharePermission>()));
    }

    [Fact]
    public void PickAccess_SingleEntry()
    {
        Assert.Equal("Read", PermissionMatrixLogic.PickAccess(new[] { Perm("u", ShareAccess.Read) }));
    }

    [Fact]
    public void PickAccess_DenyBeatsFull()
    {
        Assert.Equal("Deny", PermissionMatrixLogic.PickAccess(new[]
        {
            Perm("u", ShareAccess.Full),
            Perm("u", ShareAccess.NoAccess, deny: true),
        }));
    }

    [Fact]
    public void PickAccess_FullBeatsChange()
    {
        Assert.Equal("Full", PermissionMatrixLogic.PickAccess(new[]
        {
            Perm("u", ShareAccess.Change),
            Perm("u", ShareAccess.Full),
        }));
    }

    [Fact]
    public void PickAccess_NoAccessFlagMapsToDeny()
    {
        Assert.Equal("Deny", PermissionMatrixLogic.PickAccess(new[]
        {
            Perm("u", ShareAccess.NoAccess, deny: false),
            Perm("u", ShareAccess.Read),
        }));
    }

    // ===== BuildMatrix（按账号分组聚合）=====

    [Fact]
    public void BuildMatrix_GroupsByAccountAndFillsDash()
    {
        var shares = new[] { TestShares.Smb("Docs") };
        var accounts = new (string, string)[] { ("alice", "User"), ("bob", "User"), ("Users", "Group") };
        var perms = new List<SharePermission>
        {
            new() { shareName = "Docs", account = "alice", accountType = AccountType.User, access = ShareAccess.Read, deny = false },
            new() { shareName = "Docs", account = "alice", accountType = AccountType.User, access = ShareAccess.Full, deny = false },
            new() { shareName = "Docs", account = "bob", accountType = AccountType.User, access = ShareAccess.NoAccess, deny = true },
        };
        var matrix = PermissionMatrixLogic.BuildMatrix(
            permLists: permListsOf(shares, new[] { perms }), accounts);
        Assert.Equal("Full", matrix["Docs"]["alice"]);   // 多条取最严格但 Full>Read
        Assert.Equal("Deny", matrix["Docs"]["bob"]);
        Assert.Equal("-", matrix["Docs"]["Users"]);      // 无授权条目落 -
    }

    static IReadOnlyList<(Share share, IReadOnlyList<SharePermission> perms)> permListsOf(
        IReadOnlyList<Share> shares, IReadOnlyList<IReadOnlyList<SharePermission>>? lists)
    {
        if (lists is null) return shares.Select(s => (s, (IReadOnlyList<SharePermission>)Array.Empty<SharePermission>())).ToList();
        return shares.Zip(lists, (s, p) => (s, p)).ToList();
    }

    // ===== 宽权限账号 [Source: PermissionMatrix.tsx#isWideAccount] =====

    [Theory]
    [InlineData("Everyone", true)]
    [InlineData(@"NT AUTHORITY\Authenticated Users", true)]
    [InlineData(@"WORKGROUP\Guests", true)]
    [InlineData("admin", false)]
    [InlineData("everybody", false)]
    public void IsWideAccount(string name, bool expected)
    {
        Assert.Equal(expected, PermissionMatrixLogic.IsWideAccount(name));
    }

    // ===== HTML 转义 [Source: PermissionMatrix.tsx#escapeHtml] =====

    [Fact]
    public void EscapeHtml_AmpersandFirst()
    {
        // & 必须最先替换，否则后续实体会被二次转义
        Assert.Equal("&amp;lt;shared&amp;gt;", PermissionMatrixLogic.EscapeHtml("&lt;shared&gt;"));
        Assert.Equal("&quot;x&quot; &#39;y&#39;", PermissionMatrixLogic.EscapeHtml("\"x\" 'y'"));
        Assert.Equal("", PermissionMatrixLogic.EscapeHtml(null));
    }

    // ===== CSV 导出 [Source: PermissionMatrix.tsx#exportCsv] =====

    [Fact]
    public void BuildCsv_BomAndQuotedCells()
    {
        var shares = new[] { TestShares.Smb("Docs") };
        var accounts = new (string, string)[] { ("alice", "User") };
        var matrix = new Dictionary<string, Dictionary<string, string>>
        {
            ["Docs"] = new() { ["alice"] = "Full" },
        };
        var csv = PermissionMatrixLogic.BuildCsv(shares, accounts, matrix);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"共享\",\"alice\"", csv);
        Assert.Contains("\"Docs\",\"完全\"", csv);
    }

    // ===== JSON 导出 [Source: PermissionMatrix.tsx#exportJson] =====

    [Fact]
    public void BuildJson_Shape()
    {
        var shares = new[] { TestShares.Smb("Docs", path: @"E:\d") };
        var accounts = new (string, string)[] { ("alice", "User") };
        var matrix = new Dictionary<string, Dictionary<string, string>>
        {
            ["Docs"] = new() { ["alice"] = "Read" },
        };
        var json = PermissionMatrixLogic.BuildJson(shares, accounts, matrix, DateTimeOffset.UnixEpoch);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("1970-01-01T00:00:00.0000000Z", doc.RootElement.GetProperty("exportedAt").GetString());
        var perm = doc.RootElement.GetProperty("shares")[0].GetProperty("permissions")[0];
        Assert.Equal("alice", perm.GetProperty("account").GetString());
        Assert.Equal("User", perm.GetProperty("accountType").GetString());
        Assert.Equal("Read", perm.GetProperty("access").GetString());
    }

    // ===== HTML 报告 [Source: PermissionMatrix.tsx#buildHtmlReport] =====

    [Fact]
    public void BuildHtmlReport_SelfContainedAndEscaped()
    {
        var share = TestShares.Smb("<evil>");
        var shares = new[] { share };
        var accounts = new (string, string)[] { (@"NT AUTHORITY\Authenticated Users", "Group") };
        var matrix = new Dictionary<string, Dictionary<string, string>>
        {
            ["<evil>"] = new() { [@"NT AUTHORITY\Authenticated Users"] = "Full" },
        };
        var html = PermissionMatrixLogic.BuildHtmlReport(shares, accounts, matrix, new DateTime(2026, 1, 1));
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<title>谁能访问什么 · 共享权限审计报告</title>", html);
        // 系统字符串必须转义后才进模板
        Assert.Contains("&lt;evil&gt;", html);
        Assert.DoesNotContain("<evil>", html);
        // 宽权限命中分支（warnbox）
        Assert.Contains("宽权限账号提示", html);
        Assert.Contains("warnbox", html);
        Assert.Contains("（宽权限）", html);
        // 无外部资源（自包含约束）
        Assert.DoesNotContain("http://", html.Replace("https://", ""));
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void BuildHtmlReport_NoWideFindingsUsesOkBox()
    {
        var shares = new[] { TestShares.Smb("Docs") };
        var accounts = new (string, string)[] { ("alice", "User") };
        var matrix = new Dictionary<string, Dictionary<string, string>>
        {
            ["Docs"] = new() { ["alice"] = "Read" },
        };
        var html = PermissionMatrixLogic.BuildHtmlReport(shares, accounts, matrix, DateTime.Now);
        Assert.Contains("okbox", html);
        Assert.Contains("未发现", html);
    }
}
