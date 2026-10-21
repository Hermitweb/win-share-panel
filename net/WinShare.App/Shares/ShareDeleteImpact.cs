using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 删除影响/撤销能力文案（逐条对齐 src/utils/shareDefaults.ts 的
/// deleteImpactText / undoHintText；纯函数，xUnit 直测）：
/// 连接影响与可撤销性是运维点下删除前唯一能看到的事实——
/// SMB 删除有快照可撤销（操作回收站），非 SMB 仅留档，诚实写明不给坏按钮。
/// </summary>
public static class ShareDeleteImpact
{
    /// <summary>
    /// TS deleteImpactText：误删最怕"正在传文件的人被踢"，这句话必须在确认框里先看见。
    /// 返回 (文案, 是否 danger 红色)。
    /// </summary>
    public static (string text, bool danger) DeleteImpactText(IReadOnlyList<Share> shares)
    {
        var connected = shares.Where(s => s.concurrentUsers > 0).ToList();
        if (connected.Count == 0)
        {
            return (shares.Count == 1
                ? "当前无人连接，可安全删除。"
                : "所选共享当前均无人连接。", false);
        }

        var users = connected.Sum(s => s.concurrentUsers);
        var names = string.Join("、", connected.Take(3).Select(s => $"{s.name}({s.concurrentUsers})"));
        var more = connected.Count > 3 ? " 等" : "";
        return ($"当前 {users} 个连接正在使用{more}共享：{names}{more}——删除会强制断开这些连接，正在写入的文件可能丢失。", true);
    }

    /// <summary>
    /// TS undoHintText：只有 SMB 删除带可还原快照（journal.undoJournal 走 restoreShare），
    /// 其他协议没有通用还原通道——写明"仅留档"。
    /// </summary>
    public static string UndoHintText(IReadOnlyList<Share> shares)
    {
        var smb = shares.Count(s => s.protocol == Protocol.Smb);
        var other = shares.Count - smb;
        if (shares.Count == 1)
            return smb > 0
                ? "删除后可在「操作回收站」一键撤销。"
                : "该协议的删除仅留档，不支持一键撤销。";
        if (other == 0) return $"{smb} 个 SMB 共享删除后均可在「操作回收站」撤销。";
        if (smb == 0) return $"{other} 个非 SMB 共享仅留档，不支持一键撤销。";
        return $"其中 {smb} 个 SMB 共享可撤销；{other} 个非 SMB 共享仅留档，不支持一键撤销。";
    }
}
