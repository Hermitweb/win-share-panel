// v2.2.0 第二轮：命令面板动态实体搜索（纯函数表驱动）+ 实体命令构建。
using WinShare.App.Shell;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shell;

public class CommandPaletteSearchTests
{
    [Fact]
    public void BuildEntityCommands_AllGroups_ProducedWithHintAndRoute()
    {
        var log = new List<(string route, string? attention)>();
        var cmds = CommandPaletteSearch.BuildEntityCommands(
            shares: new[] { new Share { name = "Docs", path = @"D:\Docs", protocol = Protocol.Smb } },
            users: new[] { new LocalUser { name = "alice" } },
            groups: new[] { new LocalGroup { name = "devs" } },
            sessions: new[] { new ProtocolSession { sessionId = "alice", clientUserName = "alice", clientComputerName = "PC1", clientOpenFiles = 3 } },
            navigate: (route, attention) => log.Add((route, attention)));

        Assert.Contains(cmds, c => c.Group == "共享" && c.Label == "Docs" && c.Hint.Contains(@"D:\Docs"));
        Assert.Contains(cmds, c => c.Group == "用户" && c.Label == "alice");
        Assert.Contains(cmds, c => c.Label == "devs（组）");
        Assert.Contains(cmds, c => c.Group == "会话" && c.Label == "alice（PC1）" && c.Hint.Contains("3 个文件"));

        // 执行动作：共享 → /shares 且带 attention（经 TakeAttention 直达搜索定位）
        cmds.First(c => c.Label == "Docs").Run();
        Assert.Contains(("/shares", "Docs"), log);
        cmds.First(c => c.Label == "alice（PC1）" && c.Group == "会话").Run();
        Assert.Contains(("/sessions", (string?)null), log);
    }

    [Fact]
    public void BuildEntityCommands_NullCollections_NoFabricatedGroups()
    {
        // 服务缺席 → 对应组静默缺席（不臆造结果）
        var cmds = CommandPaletteSearch.BuildEntityCommands(null, null, null, null, (_, _) => { });
        Assert.Empty(cmds);
    }

    [Fact]
    public void Matches_GatedByMinQueryLength()
    {
        var c = new CommandPaletteWindow.Command("k", "Docs", "共享 · D:\\Docs", "共享", () => { });
        Assert.False(CommandPaletteSearch.Matches(c, "D"));        // 1 字符不参与
        Assert.False(CommandPaletteSearch.Matches(c, "zzz"));      // 不命中
        Assert.True(CommandPaletteSearch.Matches(c, "Doc"));       // Label 命中
        Assert.True(CommandPaletteSearch.Matches(c, "docs"));      // 大小写不敏感
        Assert.True(CommandPaletteSearch.Matches(c, "D:\\Docs"));  // Hint 命中
    }
}
