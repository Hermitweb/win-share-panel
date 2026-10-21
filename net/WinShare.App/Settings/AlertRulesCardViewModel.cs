using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 告警规则卡（四项：空闲提醒/SMB1/弱口令/磁盘阈值）。
/// [Source: src/components/AppSettingsCards.tsx AlertRulesCard L190-300]
/// 语义逐条对齐：
///  - 每次只提交被改的那一项（其余规则保持原值——C# store 是整体替换，VM 负责组装全量）；
///  - 磁盘阈值草稿：外部值变化草稿跟随（DiscardDraftToExternal）；失焦/回车即保存；
///  - 保存失败草稿退回当前值；
///  - idleAlertMinutes：0=关闭（store 侧 null 语义：normalize 把 0 收敛为 null）。
/// </summary>
public sealed class AlertRulesCardViewModel : SettingsCardBase
{
    readonly AppSettingsStore _store;

    public static int[] IdleOptions { get; } = [0, 15, 30, 60];
    public const int DiskMin = 1;
    public const int DiskMax = 100_000;

    public AlertRulesCardViewModel(AppSettingsStore store)
    {
        _store = store;
        var rules = store.Load().alertRules;
        Smb1Alert = rules.smb1Alert;
        WeakPasswordAlert = rules.weakPasswordAlert;
        SelectedIdle = rules.idleAlertMinutes ?? 0;
        var disk = rules.diskLowGb;
        DiskDraft = disk;
        Raise();
    }

    public int SelectedIdle { get; private set; }
    public bool Smb1Alert { get; private set; }
    public bool WeakPasswordAlert { get; private set; }

    /// <summary>磁盘阈值草稿（编辑中值；未 dirty 时=当前值）。</summary>
    public int? DiskDraft { get; private set; }

    /// <summary>当前已生效磁盘阈值（保存后回读）。</summary>
    public int DiskLowGb { get; private set; }

    public bool DiskDirty => DiskDraft is not null && DiskDraft != DiskLowGb;

    /// <summary>
    /// 渲染期外部值同步（对齐 TS useResetOnKeyChange：store 被别处改过 diskLowGb → 草稿跟随，
    /// 不改变 dirty / 保存失败退回语义）。
    /// </summary>
    public void DiscardDraftToExternal(int newExternal)
    {
        if (newExternal == DiskLowGb) return;
        DiskLowGb = newExternal;
        DiskDraft = newExternal;
        Raise();
    }

    /// <summary>输入框值收敛为整数（对齐 TS toInt：非数→null，非整→四舍五入）。</summary>
    public static int? ToInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), out var n) ? n : null;
    }

    /// <summary>设草稿（输入框 onChange）；越界值收敛回区间 [1, 100000]。</summary>
    public void SetDiskDraft(int? value)
    {
        var v = value is { } x ? Math.Clamp(x, DiskMin, DiskMax) : (int?)null;
        DiskDraft = v;
        Raise();
    }

    /// <summary>失焦/回车保存磁盘阈值（对齐 TS saveDisk：未 dirty 或草稿空则不动作）。</summary>
    public Task SaveDiskAsync()
    {
        if (!DiskDirty || DiskDraft is null) return Task.CompletedTask;
        return SaveOneAsync("磁盘阈值", r => r with { diskLowGb = DiskDraft!.Value });
    }

    public Task SaveIdleAsync(int minutes) =>
        SaveOneAsync("空闲提醒", r => r with { idleAlertMinutes = minutes == 0 ? null : minutes });

    public Task SaveSmb1Async(bool on) =>
        SaveOneAsync("SMB1 告警", r => r with { smb1Alert = on });

    public Task SaveWeakPasswordAsync(bool on) =>
        SaveOneAsync("弱口令告警", r => r with { weakPasswordAlert = on });

    async Task SaveOneAsync(string label, Func<AlertRules, AlertRules> mutate)
    {
        if (Busy) return;
        Busy = true;
        var prevDiskDraft = DiskDraft;
        try
        {
            var current = _store.Load().alertRules;
            var next = mutate(current);
            var state = _store.SaveState(new AppStatePatch { alertRules = next });
            // 写回镜像
            DiskLowGb = state.alertRules.diskLowGb;
            if (label == "空闲提醒") SelectedIdle = state.alertRules.idleAlertMinutes ?? 0;
            if (label == "SMB1 告警") Smb1Alert = state.alertRules.smb1Alert;
            if (label == "弱口令告警") WeakPasswordAlert = state.alertRules.weakPasswordAlert;
            if (label == "磁盘阈值") DiskDraft = state.alertRules.diskLowGb;
            Success = $"{label}已保存";
        }
        catch (Exception e)
        {
            if (label == "磁盘阈值") DiskDraft = DiskLowGb; // 草稿退回当前值（TS 同款语义）
            Error = $"保存失败：{e.Message}";
        }
        finally
        {
            Busy = false;
        }
        _ = prevDiskDraft;
    }
}
