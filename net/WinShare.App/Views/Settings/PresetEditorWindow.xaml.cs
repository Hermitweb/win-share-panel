using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Settings;

/// <summary>
/// 权限模板编辑器（v2.1 面板解锁：WPF 补齐 Electron PresetEditor 能力）。
/// 三种入口：新建空白（source=null）、编辑自定义（source.builtIn=false → UpdatePresetAsync）、
/// 内置模板一键「复制为自定义并编辑」（调用方先 DuplicatePresetAsync，传入副本 → UpdatePresetAsync）。
/// 内置保护不变：编辑内置永远经副本通道，原内置 id 不会被改写。
/// </summary>
public partial class PresetEditorWindow : Window
{
    // ComboBox 列候选（纯字符串；保存时映射回契约值）
    static readonly string[] TypeOptions = ["用户", "组"];
    static readonly string[] AccessOptions = ["完全控制", "更改", "只读"];

    readonly IPresetService _svc;
    /// <summary>编辑目标的 id（新建为 null → SavePresetAsync）；内置副本/自定义 → UpdatePresetAsync。</summary>
    readonly string? _editId;
    readonly ObservableCollection<PresetEntryRow> _rows = new();

    public PresetEditorWindow(IPresetService svc, PermissionPreset? source)
    {
        _svc = svc;
        _editId = source?.builtIn == true ? null : source?.id; // 内置原模板不可改：仅副本（builtIn=false）可编辑
        InitializeComponent();
        Title = source is null ? "新建权限模板" : $"编辑权限模板：{source.name}";
        TxtName.Text = source?.name ?? "";
        TxtDescription.Text = source?.description ?? "";
        foreach (PresetEntry e in source?.entries ?? Array.Empty<PresetEntry>())
        {
            _rows.Add(new PresetEntryRow(e));
        }
        if (_rows.Count == 0) _rows.Add(new PresetEntryRow(new PresetEntry { account = "", accountType = AccountType.User, access = ShareAccess.Read }));
        EntriesGrid.ItemsSource = _rows;
        // ComboBox 列候选（纯字符串，DataGridComboBoxColumn 为每格生成独立容器）
        ColType.ItemsSource = TypeOptions;
        ColAccess.ItemsSource = AccessOptions;
    }

    void OnAddEntry(object sender, RoutedEventArgs e)
    {
        var row = new PresetEntryRow(new PresetEntry { account = "", accountType = AccountType.User, access = ShareAccess.Read });
        _rows.Add(row);
        EntriesGrid.Items.Refresh();
        EntriesGrid.ScrollIntoView(row);
    }

    void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PresetEntryRow row)
        {
            _rows.Remove(row);
            EntriesGrid.Items.Refresh();
        }
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        string name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("模板名称不能为空");
            return;
        }
        var entries = new List<PresetEntry>();
        foreach (PresetEntryRow r in _rows)
        {
            string account = r.Account.Trim();
            if (account.Length == 0)
            {
                ShowError("存在账号为空的条目，请补全或删除");
                return;
            }
            entries.Add(new PresetEntry
            {
                account = account,
                accountType = r.TypeText == "组" ? AccountType.Group : AccountType.User,
                access = r.AccessText switch
                {
                    "完全控制" => ShareAccess.Full,
                    "更改" => ShareAccess.Change,
                    _ => ShareAccess.Read,
                },
                deny = r.Deny,
            });
        }
        if (entries.Count == 0)
        {
            ShowError("至少需要一条授权条目");
            return;
        }
        try
        {
            if (_editId is null)
            {
                await _svc.SavePresetAsync(new PermissionPreset
                {
                    id = $"preset-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    name = name,
                    description = TxtDescription.Text.Trim(),
                    builtIn = false,
                    entries = entries,
                    category = "自定义",
                });
            }
            else
            {
                await _svc.UpdatePresetAsync(_editId, new PermissionPreset
                {
                    id = _editId,
                    name = name,
                    description = TxtDescription.Text.Trim(),
                    entries = entries,
                });
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    void ShowError(string msg)
    {
        LblError.Text = msg;
        LblError.Visibility = Visibility.Visible;
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>条目行的可绑定形态（ComboBox 列以纯文本字符串绑定；保存时映射回契约值）。</summary>
    public sealed class PresetEntryRow
    {
        public PresetEntryRow(PresetEntry source)
        {
            Source = source;
            Account = source.account;
            Deny = source.deny == true;
            TypeText = source.accountType == AccountType.Group ? "组" : "用户";
            AccessText = source.access switch
            {
                ShareAccess.Full => "完全控制",
                ShareAccess.Change => "更改",
                _ => "只读",
            };
        }

        public PresetEntry Source { get; }
        public string Account { get; set; } = "";
        public string TypeText { get; set; } = "用户";
        public string AccessText { get; set; } = "只读";
        public bool Deny { get; set; }
    }
}
