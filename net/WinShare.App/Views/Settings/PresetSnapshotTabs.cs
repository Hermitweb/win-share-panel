// 「权限模板」与「快照历史」两个 Tab（对齐 src/pages/Settings.tsx#638-713 的两个 Tab）。
// TS 原版：权限模板表格（名称/描述/分类/类型/条目）+ 查看/复制/删除 + 导入导出；
//          快照历史（时间 + 回滚确认）。
// C# 侧服务已在 Core 落地（IPresetService / ISmbService），此处只补 UI。
using System.Windows;
using System.Windows.Controls;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>权限模板 Tab（[Source: Settings.tsx#权限模板 Tab]）。</summary>
public sealed class PresetTabView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly StackPanel _entriesBox = new();

    public PresetTabView()
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(SettingsUi.HeaderRow("权限模板",
            SettingsUi.Button("刷新", (_, _) => Reload()),
            SettingsUi.Button("导出", async (_, _) => await ExportAsync()),
            SettingsUi.Button("导入", async (_, _) => await ImportAsync(), primary: true)));
        root.Children.Add(SettingsUi.FogText(
            "内置模板不可删除（可复制）。应用到共享时按「覆盖」写入全部授权，失败自动回滚到改前权限。"));
        root.Children.Add(_list);
        root.Children.Add(SettingsUi.HeaderRow("条目预览"));
        root.Children.Add(_entriesBox);
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _list.Children.Clear();
        IPresetService? svc = SettingsServiceSlots.PresetService;
        if (svc is null)
        {
            _list.Children.Add(SettingsUi.FogText("权限模板服务未注册：等待 Core 服务层接入。"));
            return;
        }
        IReadOnlyList<PermissionPreset> presets;
        try
        {
            presets = svc.ListPresetsAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _list.Children.Add(SettingsUi.FogText($"读取权限模板失败：{e.Message}"));
            return;
        }
        if (presets.Count == 0)
        {
            _list.Children.Add(SettingsUi.FogText("暂无权限模板"));
            return;
        }

        foreach (PermissionPreset preset in presets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = preset.name,
                Width = 200,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = SettingsUi.Token("BrushText", "#1F2733"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = preset.description,
                Width = 300,
                FontSize = 12,
                Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = preset.builtIn ? "内置" : "自定义",
                Width = 60,
                FontSize = 12,
                Foreground = SettingsUi.Token("BrushPrimary", "#1677FF"),
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{preset.entries?.Count ?? 0} 条",
                Width = 60,
                FontSize = 12,
                Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
            });

            PermissionPreset captured = preset;
            row.Children.Add(SettingsUi.Button("查看", (_, _) => ShowEntries(captured)));
            row.Children.Add(SettingsUi.Button("复制", async (_, _) =>
            {
                try
                {
                    PermissionPreset copy = await svc.DuplicatePresetAsync(captured.id);
                    Reload();
                    MessageBox.Show($"已复制为「{copy.name}」", "权限模板");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "复制失败");
                }
            }));
            var del = SettingsUi.Button("删除", async (_, _) =>
            {
                MessageBoxResult ok = MessageBox.Show($"删除模板「{captured.name}」？", "删除确认", MessageBoxButton.YesNo);
                if (ok != MessageBoxResult.Yes) return;
                try
                {
                    await svc.DeletePresetAsync(captured.id);
                    Reload();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "删除失败");
                }
            });
            del.IsEnabled = !preset.builtIn;
            row.Children.Add(del);
            _list.Children.Add(row);
        }
    }

    private void ShowEntries(PermissionPreset preset)
    {
        _entriesBox.Children.Clear();
        if (preset.entries is null || preset.entries.Count == 0)
        {
            _entriesBox.Children.Add(SettingsUi.FogText("该模板没有条目"));
            return;
        }
        _entriesBox.Children.Add(SettingsUi.FogText($"模板「{preset.name}」共 {preset.entries.Count} 条："));
        foreach (PresetEntry entry in preset.entries)
        {
            _entriesBox.Children.Add(SettingsUi.FogText(
                $"· {entry.account}（{entry.accountType}）→ {entry.access}{(entry.deny == true ? "（拒绝）" : "")}"));
        }
    }

    private async Task ExportAsync()
    {
        IPresetService? svc = SettingsServiceSlots.PresetService;
        if (svc is null) { MessageBox.Show("服务未注册", "导出失败"); return; }
        try
        {
            string json = await svc.ExportPresetsAsync();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await AppServices.SaveFileWithDialogAsync($"winshare-presets-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json", bytes);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "导出失败");
        }
    }

    private async Task ImportAsync()
    {
        IPresetService? svc = SettingsServiceSlots.PresetService;
        if (svc is null) { MessageBox.Show("服务未注册", "导入失败"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 文件|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            string json = System.IO.File.ReadAllText(dlg.FileName);
            ImportReport report = await svc.ImportPresetsAsync(json);
            Reload();
            string text = $"导入 {report.imported} 条，跳过 {report.skipped} 条";
            if (report.errors is { Count: > 0 }) text += "\n" + string.Join("\n", report.errors.Take(3));
            MessageBox.Show(text, "导入结果");
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "导入失败");
        }
    }
}

/// <summary>快照历史 Tab（[Source: Settings.tsx#快照历史 Tab]）。</summary>
public sealed class SnapshotTabView : UserControl
{
    private readonly StackPanel _list = new();

    public SnapshotTabView()
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(SettingsUi.HeaderRow("快照历史",
            SettingsUi.Button("刷新", (_, _) => Reload()),
            SettingsUi.Button("立即快照", async (_, _) => await SnapshotNowAsync(), primary: true)));
        root.Children.Add(SettingsUi.FogText(
            "每次保存 SMB 服务器配置前自动留档；回滚会把当时的配置整体写回（含全部开关与高级参数）。"));
        root.Children.Add(_list);
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _list.Children.Clear();
        ISmbService? svc = SettingsServiceSlots.SmbService;
        if (svc is null)
        {
            _list.Children.Add(SettingsUi.FogText("SMB 服务未注册：快照不可用。"));
            return;
        }
        IReadOnlyList<SmbSnapshotMeta> snaps;
        try
        {
            snaps = svc.ListSnapshotsAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _list.Children.Add(SettingsUi.FogText($"读取快照失败：{e.Message}"));
            return;
        }
        if (snaps.Count == 0)
        {
            _list.Children.Add(SettingsUi.FogText("暂无快照（保存配置后会自动生成）"));
            return;
        }

        foreach (SmbSnapshotMeta snap in snaps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = snap.ts,
                Width = 260,
                FontSize = 12,
                Foreground = SettingsUi.Token("BrushText", "#1F2733"),
            });
            SmbSnapshotMeta captured = snap;
            row.Children.Add(SettingsUi.Button("回滚到此快照", async (_, _) =>
            {
                MessageBoxResult ok = MessageBox.Show(
                    $"回滚到 {captured.ts} 的配置？当前配置会被覆盖。", "回滚确认", MessageBoxButton.YesNo);
                if (ok != MessageBoxResult.Yes) return;
                try
                {
                    await svc.RollbackSnapshotAsync(captured.id);
                    Reload();
                    MessageBox.Show("已回滚", "快照");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "回滚失败");
                }
            }));
            _list.Children.Add(row);
        }
    }

    private async Task SnapshotNowAsync()
    {
        ISmbService? svc = SettingsServiceSlots.SmbService;
        if (svc is null) { MessageBox.Show("SMB 服务未注册", "快照失败"); return; }
        try
        {
            // TS: saveSnapshot 由 setConfig 前自动触发；此处手动触发一次等价快照（读当前配置并落档）
            SmbServerConfig current = await svc.GetConfigAsync();
            await svc.SetConfigAsync(current);
            Reload();
            MessageBox.Show("已生成快照", "快照");
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "快照失败");
        }
    }
}
