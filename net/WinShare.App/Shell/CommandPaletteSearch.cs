using WinShare.Core.Contracts;

namespace WinShare.App.Shell;

/// <summary>
/// 命令面板动态实体搜索（v2.2.0 第二轮：把共享/用户/会话接入 Ctrl+K，替代纯静态导航面）。
/// 纯函数可表驱动单测：给定实体集合 → 动态命令列表；执行动作经 navigate 委托注入
/// （生产=AppServices.Navigation.Navigate，测试=收集器），不做任何 IO/服务调用。
/// </summary>
public static class CommandPaletteSearch
{
    /// <summary>动态搜索的最小查询长度（对齐 TS CommandPalette query≥2 字符门控）。</summary>
    public const int MinQueryLength = 2;

    /// <summary>构建动态实体命令（任一集合为 null/空 → 对应组不出现；调用方保证「服务缺席不臆造」）。</summary>
    public static List<CommandPaletteWindow.Command> BuildEntityCommands(
        IReadOnlyList<Share>? shares,
        IReadOnlyList<LocalUser>? users,
        IReadOnlyList<LocalGroup>? groups,
        IReadOnlyList<ProtocolSession>? sessions,
        Action<string, string?> navigate)
    {
        var cmds = new List<CommandPaletteWindow.Command>();

        foreach (var s in shares ?? [])
        {
            var name = s.name;
            cmds.Add(new CommandPaletteWindow.Command(
                $"share:{s.protocol.ToString().ToLowerInvariant()}:{name}",
                name,
                $"共享 · {s.protocol.ToString().ToUpperInvariant()} · {s.path}",
                "共享",
                () => navigate("/shares", name)));
        }

        foreach (var u in users ?? [])
        {
            var name = u.name;
            var desc = string.IsNullOrWhiteSpace(u.description) ? "本地用户" : $"本地用户 · {u.description}";
            cmds.Add(new CommandPaletteWindow.Command(
                $"user:{name}",
                name,
                desc,
                "用户",
                () => navigate("/users", name)));
        }

        foreach (var g in groups ?? [])
        {
            var name = g.name;
            cmds.Add(new CommandPaletteWindow.Command(
                $"group:{name}",
                $"{name}（组）",
                "本地组",
                "用户",
                () => navigate("/users", name)));
        }

        foreach (var sess in sessions ?? [])
        {
            var who = string.IsNullOrEmpty(sess.clientComputerName)
                ? sess.clientUserName
                : $"{sess.clientUserName}（{sess.clientComputerName}）";
            cmds.Add(new CommandPaletteWindow.Command(
                $"session:{sess.protocol.ToString().ToLowerInvariant()}:{sess.sessionId}",
                who,
                $"{sess.protocol.ToString().ToUpperInvariant()} 会话 · 打开 {sess.clientOpenFiles} 个文件",
                "会话",
                () => navigate("/sessions", null)));
        }

        return cmds;
    }

    /// <summary>动态结果过滤（≥2 字符才参与；Label/Hint 子串命中，大小写不敏感）。</summary>
    public static bool Matches(CommandPaletteWindow.Command c, string query) =>
        query.Length >= MinQueryLength &&
        (c.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
         c.Hint.Contains(query, StringComparison.OrdinalIgnoreCase));
}
