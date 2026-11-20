// 「权限模板」与「快照历史」两个 Tab（对齐 src/pages/Settings.tsx#638-713 的两个 Tab）。
// TS 原版：权限模板表格（名称/描述/分类/类型/条目）+ 查看/复制/删除 + 导入导出；
//          快照历史（时间 + 回滚确认）。
// C# 侧服务已在 Core 落地（IPresetService / ISmbService），此处只补 UI。
using System.Windows;
using System.Windows.Controls;
using WinShare.App.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>权限模板 Tab（[Source: Settings.tsx#权限模板 Tab]；v2.1 补齐编辑器与新建空白模板）。</summary>
public sealed class PresetTabView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly StackPanel _entriesBox = new();

    public PresetTabView()
    {
        var root = new StackPanel(); // v2.2.0：外层 Card 已有 padding，去掉双层缩进
        // v2.2.0 第二轮：去掉与外层 CardTitle 重复的「权限模板」标题，仅保留按钮行
        root.Children.Add(SettingsUi.HeaderRow("", 
            SettingsUi.Button("刷新", (_, _) => Reload()),
            SettingsUi.Button("新建空白模板", (_, _) => _ = NewBlankAsync()),
            SettingsUi.Button("导出", async (_, _) => await ExportAsync()),
            SettingsUi.Button("导入", async (_, _) => await ImportAsync(), primary: true)));
        root.Children.Add(SettingsUi.FogText("内置模板不可删除；应用时覆盖全部授权，失败自动回滚。"));
        root.Children.Add(_list);
        root.Children.Add(SettingsUi.HeaderRow("条目预览"));
        root.Children.Add(_entriesBox);
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) => Reload();
    }

    /// <summary>v2.1：新建空白模板（编辑器 → SavePresetAsync）。</summary>
    private async Task NewBlankAsync()
    {
        IPresetService? svc = SettingsServiceSlots.PresetService;
        if (svc is null) { MessageBox.Show("权限模板服务未注册", "新建失败"); return; }
        var win = new PresetEditorWindow(svc, null) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Reload();
        await Task.CompletedTask;
    }

    /// <summary>
    /// v2.1：编辑模板。自定义 → 直接进入编辑器（UpdatePresetAsync）；
    /// 内置 → 先 DuplicatePresetAsync 成副本再进入编辑器（内置保护语义不变）。
    /// </summary>
    private async Task EditAsync(IPresetService svc, PermissionPreset preset)
    {
        PermissionPreset target = preset;
        if (preset.builtIn)
        {
            try
            {
                target = await svc.DuplicatePresetAsync(preset.id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "复制内置模板失败");
                return;
            }
        }
        var win = new PresetEditorWindow(svc, target) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
        {
            Reload();
            MessageBox.Show($"模板「{target.name}」已保存", "权限模板");
        }
    }

    private async void Reload()
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
            // 异步加载：绝不在 UI 线程 GetResult（那正是「进入页面卡一下」的根因）
            presets = await svc.ListPresetsAsync();
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
            // v2.2.0 第二轮：名称/类型/条目数一行、说明一行、操作按钮一行（此前全部纵排，宽屏挤成左窄柱）
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 6) };

            var meta = new StackPanel { Orientation = Orientation.Horizontal };
            meta.Children.Add(new TextBlock
            {
                Text = preset.name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            }.Live(TextBlock.ForegroundProperty, "BrushText"));
            meta.Children.Add(new TextBlock
            {
                Text = preset.builtIn ? "内置" : "自定义",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            }.Live(TextBlock.ForegroundProperty, "BrushPrimary"));
            meta.Children.Add(new TextBlock
            {
                Text = $"{preset.entries?.Count ?? 0} 条",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            }.Live(TextBlock.ForegroundProperty, "BrushFog"));
            row.Children.Add(meta);

            row.Children.Add(new TextBlock
            {
                Text = preset.description,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 4),
            }.Live(TextBlock.ForegroundProperty, "BrushFog"));

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            PermissionPreset captured = preset;
            btns.Children.Add(SettingsUi.Button("查看", (_, _) => ShowEntries(captured)));
            // v2.1：编辑入口（自定义直接编辑；内置自动复制为自定义副本后编辑）
            btns.Children.Add(SettingsUi.Button(preset.builtIn ? "复制并编辑" : "编辑", async (_, _) =>
            {
                try
                {
                    await EditAsync(svc, captured);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "编辑失败");
                }
            }));
            btns.Children.Add(SettingsUi.Button("复制", async (_, _) =>
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
            del.ToolTip = preset.builtIn ? "内置模板不可删除（可复制为自定义）" : null;
            btns.Children.Add(del);
            row.Children.Add(btns);
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
        var root = new StackPanel(); // v2.2.0：外层 Card 已有 padding，去掉双层缩进
        // v2.2.0 第二轮：去掉与外层 CardTitle 重复的「快照历史」标题
        root.Children.Add(SettingsUi.HeaderRow("",
            SettingsUi.Button("刷新", (_, _) => Reload()),
            SettingsUi.Button("立即快照", async (_, _) => await SnapshotNowAsync(), primary: true)));
        root.Children.Add(SettingsUi.FogText("保存 SMB 配置前自动留档，可整体回滚。"));
        root.Children.Add(_list);
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) => Reload();
    }

    private async void Reload()
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
            snaps = await svc.ListSnapshotsAsync();
        }
        catch (Exception e)
        {
            _list.Children.Add(SettingsUi.FogText($"读取快照失败：{e.Message}"));
            return;
        }
        if (snaps.Count == 0)
        {
            _list.Children.Add(SettingsUi.FogText("暂无快照"));
            return;
        }

        foreach (SmbSnapshotMeta snap in snaps)
        {
            // v2.2.0 第二轮：时间与回滚按钮一行（此前纵排两行）
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = snap.ts,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            }.Live(TextBlock.ForegroundProperty, "BrushText"));

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
