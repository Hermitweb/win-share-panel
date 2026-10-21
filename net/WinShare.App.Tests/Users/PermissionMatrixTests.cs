using WinShare.App.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Users;

/// <summary>
/// 权限矩阵纯函数（逐行对齐 src/components/PermissionMatrix.tsx）：
/// PickAccess（Deny 优先安全语义）/ IsWideAccount / EscapeHtml / CSV/JSON/HTML 报告 / mapPool。
/// </summary>
public class PermissionMatrixTests
{
    [Source("[Source: src/components/PermissionMatrix.tsx pickAccess]")]
    [Fact]
    public void PickAccess_空列表为None()
    {
        Assert.Equal(MatrixAccess.None, PermissionMatrix.PickAccess(Array.Empty<SharePermission>()));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx pickAccess]")]
    [Fact]
    public void PickAccess_多条取最严格_Deny优先()
    {
        var perms = new[]
        {
            TestUsers.Perm("s1", "alice", ShareAccess.Read),
            TestUsers.Perm("s1", "alice", ShareAccess.Full),
        };
        Assert.Equal(MatrixAccess.Full, PermissionMatrix.PickAccess(perms)); // Full > Read

        var deny = new[]
        {
            TestUsers.Perm("s1", "alice", ShareAccess.Full),
            TestUsers.Perm("s1", "alice", ShareAccess.NoAccess, deny: true),
        };
        Assert.Equal(MatrixAccess.Deny, PermissionMatrix.PickAccess(deny));

        var readChange = new[]
        {
            TestUsers.Perm("s1", "alice", ShareAccess.Read),
            TestUsers.Perm("s1", "alice", ShareAccess.Change),
        };
        Assert.Equal(MatrixAccess.Change, PermissionMatrix.PickAccess(readChange));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx pickAccess]")]
    [Fact]
    public void PickAccess_NoAccess映射Deny且deny标志直通()
    {
        Assert.Equal(MatrixAccess.Deny,
            PermissionMatrix.PickAccess(new[]
            {
                TestUsers.Perm("s", "a", ShareAccess.NoAccess),
                TestUsers.Perm("s", "a", ShareAccess.Read),
            }));
        Assert.Equal(MatrixAccess.Deny,
            PermissionMatrix.PickAccess(new[]
            {
                TestUsers.Perm("s", "a", ShareAccess.Full, deny: true),
                TestUsers.Perm("s", "a", ShareAccess.Full),
            }));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx isWideAccount]")]
    [Theory]
    [InlineData("Everyone", true)]
    [InlineData("everyone", true)]
    [InlineData("EVERYONE ", true)]
    [InlineData("NT AUTHORITY\\Authenticated Users", true)]
    [InlineData("DOMAIN\\Guests", true)]
    [InlineData("Nt Authority\\AUTHENTICATED USERS", true)]
    [InlineData("Administrator", false)]
    [InlineData("my", false)]
    [InlineData("", false)]
    public void IsWideAccount_宽权限账号判定(string name, bool expected)
    {
        Assert.Equal(expected, PermissionMatrix.IsWideAccount(name));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx escapeHtml]")]
    [Fact]
    public void EscapeHtml_amp符号最先替换()
    {
        // & 最先替换："<b>"&x"'" → &lt;b&gt;&quot;&amp;x&quot;&#39;
        Assert.Equal("&lt;b&gt;&quot;&amp;x&quot;&#39;", PermissionMatrix.EscapeHtml("<b>\"&x\"'"));
        Assert.Equal("", PermissionMatrix.EscapeHtml(null));
        Assert.Equal("123", PermissionMatrix.EscapeHtml(123));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx exportCsv]")]
    [Fact]
    public void BuildMatrixCsv_BOM引号转义与行列结构()
    {
        var shares = new List<Share> { TestUsers.Share("s1"), TestUsers.Share("s2") };
        var accounts = new List<MatrixAccount> { new("alice", "User"), new("devs", "Group") };
        var matrix = new Dictionary<string, Dictionary<string, MatrixAccess>>
        {
            ["s1"] = new() { ["alice"] = MatrixAccess.Full, ["devs"] = MatrixAccess.None },
            ["s2"] = new() { ["alice"] = MatrixAccess.Read, ["devs"] = MatrixAccess.Deny },
        };
        var csv = PermissionMatrix.BuildMatrixCsv(shares, accounts, matrix);
        Assert.StartsWith("\uFEFF", csv);
        var lines = csv.Substring(1).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("\"共享\",\"alice\",\"devs\"", lines[0]);
        Assert.Equal("\"s1\",\"完全\",\"-\"", lines[1]);
        Assert.Equal("\"s2\",\"只读\",\"拒绝\"", lines[2]);
    }

    [Source("[Source: src/components/PermissionMatrix.tsx exportJson]")]
    [Fact]
    public void BuildMatrixJson_access用英文字面量()
    {
        var shares = new List<Share> { TestUsers.Share("s1") };
        var accounts = new List<MatrixAccount> { new("alice", "User") };
        var matrix = new Dictionary<string, Dictionary<string, MatrixAccess>>
        {
            ["s1"] = new() { ["alice"] = MatrixAccess.Change },
        };
        var json = PermissionMatrix.BuildMatrixJson(shares, accounts, matrix, DateTimeOffset.UtcNow);
        Assert.Contains("\"exportedAt\":", json);
        Assert.Contains("\"access\": \"Change\"", json);
        Assert.Contains("\"accountType\": \"User\"", json);
        Assert.Contains("\"path\": \"C:\\\\data\"", json); // 路径转义
    }

    [Source("[Source: src/components/PermissionMatrix.tsx buildHtmlReport]")]
    [Fact]
    public void BuildHtmlReport_宽权限命中走warnbox_未命中走okbox()
    {
        var shares = new List<Share> { TestUsers.Share("pub") };
        var wide = new List<MatrixAccount> { new("Everyone", "Group") };
        var matrix = new Dictionary<string, Dictionary<string, MatrixAccess>>
        {
            ["pub"] = new() { ["Everyone"] = MatrixAccess.Full },
        };
        var html = PermissionMatrix.BuildHtmlReport(shares, wide, matrix, DateTimeOffset.UtcNow);
        Assert.Contains("warnbox", html);
        Assert.Contains("重点复核", html);
        Assert.Contains("谁能访问什么 · 共享权限审计报告", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("图例：完全 = 读取/写入/删除/改权限", html);
        Assert.Contains("边界说明", html);
        Assert.Equal("拒绝", PermissionMatrix.AccessLabel(MatrixAccess.Deny));

        var clean = new Dictionary<string, Dictionary<string, MatrixAccess>>
        {
            ["pub"] = new() { ["Everyone"] = MatrixAccess.None },
        };
        Assert.Contains("okbox", PermissionMatrix.BuildHtmlReport(shares, wide, clean, DateTimeOffset.UtcNow));
    }

    [Source("[Source: src/components/PermissionMatrix.tsx buildHtmlReport（escapeHtml 注入防护）]")]
    [Fact]
    public void BuildHtmlReport_系统字符串经EscapeHtml()
    {
        var shares = new List<Share> { TestUsers.Share("<img onerror=x>", "C:\\x") };
        var accounts = new List<MatrixAccount> { new("a<b", "User") };
        var matrix = new Dictionary<string, Dictionary<string, MatrixAccess>>
        {
            ["<img onerror=x>"] = new() { ["a<b"] = MatrixAccess.None },
        };
        var html = PermissionMatrix.BuildHtmlReport(shares, accounts, matrix, DateTimeOffset.UtcNow);
        Assert.Contains("&lt;img onerror=x&gt;", html);
        Assert.Contains("a&lt;b", html);
        Assert.DoesNotContain("a<b", html);
    }

    [Source("[Source: src/components/PermissionMatrix.tsx mapPool]")]
    [Fact]
    public async Task MapPool_结果顺序与输入一致()
    {
        var input = Enumerable.Range(1, 10).ToList();
        var results = await PermissionMatrix.MapPool(input, 4,
            i => Task.Run(() => i * 2), () => false);
        Assert.Equal(input.Select(i => i * 2).ToList(), results);
    }

    [Source("[Source: src/components/PermissionMatrix.tsx mapPool]")]
    [Fact]
    public async Task MapPool_并发上限生效()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new object();
        var results = await PermissionMatrix.MapPool(Enumerable.Range(1, 9).ToList(), 3,
            async _ =>
            {
                var current = Interlocked.Increment(ref inFlight);
                lock (gate) maxInFlight = Math.Max(maxInFlight, current);
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return 0;
            },
            () => false);
        Assert.Equal(9, results.Count);
        Assert.True(maxInFlight <= 3, $"并发峰值 {maxInFlight} 超过上限 3");
    }

    [Source("[Source: src/components/PermissionMatrix.tsx mapPool]")]
    [Fact]
    public async Task MapPool_取消后停止派发()
    {
        var counter = 0;
        var cancelled = false;
        await PermissionMatrix.MapPool(Enumerable.Range(1, 100).ToList(), 4,
            async _ =>
            {
                Interlocked.Increment(ref counter);
                await Task.Delay(50);
                if (counter >= 4) cancelled = true; // 前 4 项完成后取消
                return 0;
            },
            () => cancelled);
        Assert.True(counter < 100, $"取消后仍在派发全部任务（counter={counter}）");
    }
}
