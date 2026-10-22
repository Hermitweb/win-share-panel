using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinShare.App.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Views.Users;

/// <summary>
/// 用户/组模态框（TS antd Modal/Drawer 的 WPF 对应面；纯代码构建，无 XAML）：
/// 表单校验走 UsersValidators（与 TS Form rules 对齐），写路径走 UsersViewModel 方法
/// （服务缺席时如实报错不崩）。约定：Operated=true 表示窗口期内发生过成功写操作 → 宿主页刷新。
/// </summary>

/// <summary>批量分配到组（对齐 Users.tsx 底部 Modal：选择目标组后才可确认；已在组中的跳过）。</summary>
public sealed class BatchAssignDialog : Window
{
    public string SelectedGroup { get; private set; } = "";
    public bool Operated { get; private set; }

    public BatchAssignDialog(IReadOnlyList<string> selectedUsers, IReadOnlyList<LocalGroup> groups)
    {
        Title = $"分配 {selectedUsers.Count} 个用户到组";
        Width = 420;
        SizeToContent = SizeToContent.Height;

        var okBtn = new Button { Content = "分配", IsDefault = true, IsEnabled = false, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };

        var combo = new ComboBox { FontSize = 13 };
        foreach (var g in groups)
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{g.name}{(string.IsNullOrEmpty(g.description) ? "" : $" ({g.description})")}",
                Tag = g.name,
            });
        combo.SelectionChanged += (_, _) =>
        {
            SelectedGroup = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            okBtn.IsEnabled = SelectedGroup.Length > 0;
        };
        okBtn.Click += (_, _) =>
        {
            if (SelectedGroup.Length == 0) return;
            Operated = true;
            DialogResult = true;
            Close();
        };

        // 1 列纵向流：用户列表自上而下堆叠
        var tags = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var u in selectedUsers)
        {
            var tag = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
            };
            tag.Child = new TextBlock { Text = u, FontSize = 12 };
            tags.Children.Add(tag);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = "将以下用户批量加入指定组（已在该组中的用户会跳过）：",
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap,
                },
                tags,
                combo,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { okBtn, cancelBtn },
                },
            },
        };
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>新建本地组（对齐 GroupCreateModal：组名规则 + 可选描述）。</summary>
public sealed class GroupCreateDialog : Window
{
    public new string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool Operated { get; private set; }

    public GroupCreateDialog(UsersViewModel vm)
    {
        Title = "新建本地组";
        Width = 420;
        SizeToContent = SizeToContent.Height;

        var nameBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8), ToolTip = "如 ProjectEditors" };
        var descBox = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 52,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8),
            ToolTip = "组用途描述（可选）",
        };
        var err = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.Red,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var okBtn = new Button { Content = "创建", MinWidth = 80 };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };

        okBtn.Click += async (_, _) =>
        {
            Name = nameBox.Text.Trim();
            Description = descBox.Text.Trim();
            var problem = UsersValidators.ValidateGroupName(Name);
            if (problem is not null)
            {
                err.Text = problem;
                err.Visibility = Visibility.Visible;
                return;
            }
            okBtn.IsEnabled = false;
            var (ok, msg) = await vm.CreateGroupAsync(new CreateGroupOpts { name = Name, description = Description });
            okBtn.IsEnabled = true;
            if (ok)
            {
                Operated = true;
                DialogResult = true;
                Close();
            }
            else
            {
                err.Text = msg;
                err.Visibility = Visibility.Visible;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "组名", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) },
                nameBox,
                new TextBlock { Text = "描述", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) },
                descBox,
                err,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okBtn, cancelBtn },
                },
            },
        };
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>
/// 新建本地用户（对齐 UserCreateModal handleCreate：密码=确认密码；创建后逐组分配，
/// 组分配失败仅 warning 不整体失败；默认 启用/允许改密/永不过期）。
/// </summary>
public sealed class UserCreateDialog : Window
{
    public bool Operated { get; private set; }

    readonly UsersViewModel _vm;

    public UserCreateDialog(UsersViewModel vm)
    {
        _vm = vm;
        Title = "新建本地用户";
        Width = 520;
        SizeToContent = SizeToContent.Height;

        var nameBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), ToolTip = "如 john.doe" };
        var pwdBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4) };
        var pwdStrength = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        // 1 列纵向流：生成按钮独立一行（原在密码框右侧）
        var genBtn = new Button { Content = "⚡ 生成密码", Padding = new Thickness(8, 2, 8, 2), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "生成 12 位随机强密码" };
        var confirmBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4) };
        var fullNameBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), ToolTip = "用户显示名（可选）" };
        var descBox = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            AcceptsReturn = true,
            MinHeight = 44,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = "账号用途描述（可选）",
        };
        // 1 列纵向流：开关项自上而下堆叠
        var enabledChk = new CheckBox { Content = "启用账号", IsChecked = true, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
        var mayChangeChk = new CheckBox { Content = "允许用户修改密码", IsChecked = true, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
        var neverExpChk = new CheckBox { Content = "密码永不过期", IsChecked = true, FontSize = 13 };

        // 分配到组（多选 checkbox；对齐 TS 多选 Select；1 列纵向流）
        var groupChks = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var g in vm.Groups)
            groupChks.Children.Add(new CheckBox
            {
                Content = $"{g.name}{(string.IsNullOrEmpty(g.description) ? "" : $" ({g.description})")}",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Tag = g.name,
            });

        var err = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.Red,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        var okBtn = new Button { Content = "创建", MinWidth = 80, IsDefault = true };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        okBtn.Click += async (_, _) =>
        {
            var problem = UsersValidators.ValidateUserName(nameBox.Text.Trim());
            if (problem is not null) { err.Text = problem; err.Visibility = Visibility.Visible; return; }
            if (string.IsNullOrEmpty(pwdBox.Text)) { err.Text = "密码不能为空"; err.Visibility = Visibility.Visible; return; }
            if (pwdBox.Text != confirmBox.Text) { err.Text = "两次输入的密码不一致"; err.Visibility = Visibility.Visible; return; }
            err.Visibility = Visibility.Collapsed;
            okBtn.IsEnabled = false;
            var picked = groupChks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            var opts = new CreateUserOpts
            {
                name = nameBox.Text.Trim(),
                password = pwdBox.Text,
                fullName = fullNameBox.Text.Trim(),
                description = descBox.Text.Trim(),
                enabled = enabledChk.IsChecked == true,
                passwordChangeable = mayChangeChk.IsChecked == true,
                passwordExpires = !neverExpChk.IsChecked,
            };
            var result = await _vm.CreateUserAsync(opts, picked);
            okBtn.IsEnabled = true;
            if (result.ok)
            {
                Operated = true;
                DialogResult = true;
                Close();
            }
            else
            {
                err.Text = result.message;
                err.Visibility = Visibility.Visible;
            }
        };

        void UpdateStrength()
        {
            var s = PasswordGen.Evaluate(pwdBox.Text);
            pwdStrength.Text = s switch
            {
                "weak" => "弱",
                "medium" => "中",
                "strong" => "强",
                _ => "",
            };
            pwdStrength.Foreground = s switch
            {
                "weak" => Brushes.Red,
                "medium" => Brushes.Orange,
                "strong" => Brushes.Green,
                _ => Brushes.Gray,
            };
        }
        pwdBox.TextChanged += (_, _) => UpdateStrength();
        genBtn.Click += (_, _) =>
        {
            var pwd = PasswordGen.Generate(12);
            pwdBox.Text = pwd;
            confirmBox.Text = pwd;
            UpdateStrength();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "用户名", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) },
                nameBox,
                new TextBlock { Text = "密码", FontSize = 12, Margin = new Thickness(0, 6, 0, 2) },
                // 1 列纵向流：密码框与生成按钮各占一行
                new StackPanel
                {
                    Children = { pwdBox, genBtn },
                },
                pwdStrength,
                new TextBlock { Text = "确认密码", FontSize = 12, Margin = new Thickness(0, 4, 0, 2) },
                confirmBox,
                new TextBlock { Text = "全名", FontSize = 12, Margin = new Thickness(0, 6, 0, 2) },
                fullNameBox,
                new TextBlock { Text = "描述", FontSize = 12, Margin = new Thickness(0, 6, 0, 2) },
                descBox,
                // 1 列纵向流：开关项各占一行（原横排）
                new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { enabledChk, mayChangeChk, neverExpChk } },
                new TextBlock { Text = "分配到组（可选）", FontSize = 12, Margin = new Thickness(0, 8, 0, 2) },
                groupChks,
                err,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okBtn, cancelBtn },
                },
            },
        };
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

/// <summary>
/// 管理组（对齐 GroupManageModal Drawer）：描述保存 / 重命名 / 手动添加成员 /
/// 本地用户批量添加 / 成员移除。增删后按组名重取成员。
/// </summary>
public sealed class GroupManageDialog : Window
{
    public bool Operated { get; private set; }

    readonly UsersViewModel _vm;
    string _groupName;
    LocalGroup? _group;

    readonly TextBox _descBox;
    readonly TextBox _renameBox;
    readonly TextBox _addBox;
    readonly DataGrid _batchGrid;
    readonly ListBox _membersList;
    readonly ComboBox _removeCombo;

    public GroupManageDialog(LocalGroup group, UsersViewModel vm)
    {
        _vm = vm;
        _groupName = group.name;
        _group = group;
        Title = $"管理组：{group.name}";
        Width = 560;
        Height = 620;

        _descBox = new TextBox
        {
            Text = group.description,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            AcceptsReturn = true,
            MinHeight = 48,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 1 列纵向流：按钮左对齐独占一行
        var saveDescBtn = new Button { Content = "保存描述", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left };
        saveDescBtn.Click += async (_, _) =>
        {
            if (await _vm.UpdateGroupDescriptionAsync(_groupName, _descBox.Text.Trim()) is { ok: true })
                Operated = true;
        };

        _renameBox = new TextBox { Text = group.name, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4) };
        var renameBtn = new Button { Content = "重命名", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        renameBtn.Click += async (_, _) =>
        {
            var (ok, _) = await _vm.RenameGroupAsync(_groupName, _renameBox.Text);
            if (ok && _renameBox.Text.Trim() != group.name)
            {
                Operated = true;
                _groupName = _renameBox.Text.Trim();
                Title = $"管理组：{_groupName}";
                await ReloadAfterChange();
            }
        };

        _addBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4), ToolTip = "手动输入用户名或组名（兼容域账号）" };
        var addBtn = new Button { Content = "添加", MinWidth = 70, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        _addBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) _ = AddMemberAsync();
        };
        addBtn.Click += async (_, _) => await AddMemberAsync();

        // 本地用户批量添加（可选用户 = 未在组中的本地用户，对齐 availableUsers）
        _batchGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            SelectionUnit = DataGridSelectionUnit.Cell,
            Height = 80,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _batchGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "添加", Binding = new Binding("Pick") });
        _batchGrid.Columns.Add(new DataGridTextColumn { Header = "用户名", Binding = new Binding("Name"), IsReadOnly = true });
        _batchGrid.Columns.Add(new DataGridTextColumn { Header = "全名", Binding = new Binding("FullName"), IsReadOnly = true });
        var batchBtn = new Button { Content = "批量添加", MinWidth = 90 };
        batchBtn.Click += async (_, _) =>
        {
            var picked = _batchGrid.Items.Cast<object>()
                .OfType<BatchUserItem>().Where(i => i.Pick).Select(i => i.Name).ToList();
            if (picked.Count == 0) return;
            var result = await _vm.BatchAddGroupMembersAsync(_groupName, picked);
            if (result.Ok > 0 || result.Failed > 0)
            {
                Operated = true;
                await ReloadAfterChange();
            }
        };

        _membersList = new ListBox { FontSize = 12, Height = 120 };
        _removeCombo = new ComboBox { FontSize = 13, Width = 220, Margin = new Thickness(0, 0, 0, 4) };
        var removeBtn = new Button { Content = "移除", MinWidth = 60, HorizontalAlignment = HorizontalAlignment.Left };
        removeBtn.Click += async (_, _) =>
        {
            var m = _removeCombo.SelectedItem as string;
            if (m is null) return;
            if (await _vm.RemoveGroupMemberAsync(_groupName, m) is { ok: true })
            {
                Operated = true;
                await ReloadAfterChange();
            }
        };

        var root = new StackPanel { Margin = new Thickness(16) };
        // 1 列纵向流：描述框与保存按钮各占一行（原横排包装去掉）
        root.Children.Add(new TextBlock { Text = "描述", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_descBox);
        saveDescBtn.Margin = new Thickness(0, 4, 0, 12);
        root.Children.Add(saveDescBtn);
        // 1 列纵向流：输入框与按钮各占一行（原横排）
        root.Children.Add(new TextBlock { Text = "组名（重命名）", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_renameBox);
        root.Children.Add(renameBtn);
        root.Children.Add(new TextBlock { Text = "手动添加成员（兼容域账号）", FontSize = 12, Margin = new Thickness(0, 12, 0, 4) });
        root.Children.Add(_addBox);
        root.Children.Add(addBtn);
        root.Children.Add(new TextBlock { Text = "从本地用户批量添加", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_batchGrid);
        root.Children.Add(batchBtn);
        root.Children.Add(new TextBlock { Text = "当前成员（名称 · 类型 · 来源）", FontSize = 12, Margin = new Thickness(0, 12, 0, 4) });
        root.Children.Add(_membersList);
        // 1 列纵向流：移除成员区自上而下堆叠（原横排标签+下拉+按钮）
        root.Children.Add(new TextBlock { Text = "移除成员：", FontSize = 12, Margin = new Thickness(0, 8, 0, 4) });
        root.Children.Add(_removeCombo);
        root.Children.Add(removeBtn);
        Content = root;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        LoadMembers();
        LoadBatchOptions();

        // 成员重取（按当前组名；重命名/增删后调用）
        async Task ReloadAfterChange()
        {
            await _vm.RefreshAsync();
            LoadMembers();
            LoadBatchOptions();
        }
        async Task AddMemberAsync()
        {
            if (_addBox.Text.Trim().Length == 0) return;
            if (await _vm.AddGroupMemberAsync(_groupName, _addBox.Text) is { ok: true })
            {
                Operated = true;
                _addBox.Clear();
                await ReloadAfterChange();
            }
        }
    }

    void LoadMembers()
    {
        _group = _vm.Groups.FirstOrDefault(g => g.name == _groupName) ?? _group;
        _membersList.Items.Clear();
        _removeCombo.Items.Clear();
        foreach (var m in _group?.members ?? new List<GroupMember>())
        {
            // 成员行文本：名称 · 类型（用户/组）· 来源（对齐 TS 成员列展示）
            var memberType = m.objectClass == MemberObjectClass.Group ? "组" : "用户";
            var memberSource = string.IsNullOrEmpty(m.principalSource) ? "Local" : m.principalSource;
            _membersList.Items.Add($"{m.name} · {memberType} · {memberSource}");
            _removeCombo.Items.Add(m.name);
        }
    }

    void LoadBatchOptions()
    {
        var memberNames = new HashSet<string>(_group?.members?.Select(m => m.name) ?? new List<string>());
        _batchGrid.ItemsSource = _vm.Users
            .Where(u => !memberNames.Contains(u.name))
            .Select(u => (object)new BatchUserItem(u.name, u.fullName))
            .ToList();
    }

    /// <summary>本地用户批量项（勾选 + 名称/全名；对齐 TS availableUsers 选项）。</summary>
    public sealed class BatchUserItem
    {
        public bool Pick { get; set; }
        public string Name { get; }
        public string FullName { get; }
        public BatchUserItem(string name, string fullName)
        {
            Name = name;
            FullName = fullName;
        }
    }
}

/// <summary>
/// 用户详情（对齐 UserDetailDrawer 三 Tab）：
/// 属性（改名→UpdateUser→可选重设密码，含密码强度）/ 所属组管理 / 该用户跨共享权限查看。
/// </summary>
public sealed class UserEditDialog : Window
{
    public bool Operated { get; private set; }

    readonly UsersViewModel _vm;
    readonly LocalUser _user;
    string _userName;

    readonly TextBox _nameBox;
    readonly TextBlock _hint;
    readonly TextBox _fullNameBox;
    readonly TextBox _descBox;
    readonly CheckBox _enabledChk;
    readonly CheckBox _mayChangeChk;
    readonly CheckBox _neverExpiresChk;
    readonly TextBox _newPwdBox;
    readonly CheckBox _pwdSwitch;
    readonly TextBlock _pwdStrength;
    readonly ListBox _currentGroups;
    readonly ComboBox _addGroupCombo;
    readonly ComboBox _removeCombo;
    readonly DataGrid _permGrid;

    public UserEditDialog(LocalUser user, UsersViewModel vm)
    {
        _vm = vm;
        _user = user;
        _userName = user.name;
        Title = $"用户详情：{user.name}";
        Width = 560;
        Height = 620;

        // ===== 属性 Tab =====
        _nameBox = new TextBox { Text = user.name, FontSize = 13, Padding = new Thickness(6, 4, 6, 4) };
        _hint = new TextBlock { FontSize = 12, Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
        _nameBox.TextChanged += (_, _) =>
        {
            var v = _nameBox.Text;
            if (v == _userName)
            {
                _hint.Visibility = Visibility.Collapsed;
                return;
            }
            var problem = UsersValidators.ValidateUserName(v);
            _hint.Text = problem ?? "用户名已修改，保存时将执行重命名";
            _hint.Foreground = problem is null ? Brushes.OrangeRed : Brushes.Red;
            _hint.Visibility = Visibility.Visible;
        };
        _fullNameBox = new TextBox { Text = user.fullName, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), ToolTip = "用户显示名" };
        _descBox = new TextBox
        {
            Text = user.description,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            AcceptsReturn = true,
            MinHeight = 44,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 1 列纵向流：各开关项独立一行（原横排 16px 间距改底部 4px）
        _enabledChk = new CheckBox { Content = "启用账号", IsChecked = user.enabled, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
        _mayChangeChk = new CheckBox { Content = "允许修改密码", IsChecked = user.userMayChangePassword, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
        _neverExpiresChk = new CheckBox { Content = "密码永不过期", IsChecked = !user.passwordExpires, FontSize = 13 };
        _newPwdBox = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Width = 220, Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        _pwdSwitch = new CheckBox { Content = "重设密码", FontSize = 12, IsChecked = false, Margin = new Thickness(0, 4, 0, 0) };
        _pwdSwitch.Checked += (_, _) =>
        {
            _newPwdBox.Visibility = Visibility.Visible;
            UpdateStrength();
        };
        _pwdSwitch.Unchecked += (_, _) => _newPwdBox.Visibility = Visibility.Collapsed;
        _newPwdBox.TextChanged += (_, _) => UpdateStrength();
        _pwdStrength = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var genPwdBtn = new Button { Content = "生成", MinWidth = 56, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        genPwdBtn.ToolTip = "生成 12 位随机强密码";
        genPwdBtn.Click += (_, _) =>
        {
            _newPwdBox.Text = PasswordGen.Generate(12);
            UpdateStrength();
        };

        var saveBtn = new Button { Content = "保存", MinWidth = 90, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        saveBtn.Click += async (_, _) =>
        {
            var problem = UsersValidators.ValidateUserName(_nameBox.Text.Trim());
            if (problem is not null)
            {
                _hint.Text = problem;
                _hint.Visibility = Visibility.Visible;
                return;
            }
            var args = new UsersViewModel.UserEditArgs(
                NewName: _nameBox.Text.Trim(),
                FullName: _fullNameBox.Text.Trim(),
                Description: _descBox.Text.Trim(),
                Enabled: _enabledChk.IsChecked == true,
                UserMayChangePassword: _mayChangeChk.IsChecked == true,
                PasswordNeverExpires: _neverExpiresChk.IsChecked == true,
                NewPassword: _pwdSwitch.IsChecked == true ? _newPwdBox.Text : null);
            var (ok, _) = await _vm.SaveUserAsync(_userName, args);
            if (ok)
            {
                Operated = true;
                DialogResult = true;
                Close();
            }
        };

        var detail = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text =
                $"SID：{(string.IsNullOrEmpty(user.sid) ? "-" : user.sid)}\n" +
                $"来源：{user.principalSource ?? "Local"}\n" +
                $"上次设置密码：{UsersViewModel.FmtTime(user.passwordLastSet, "yyyy-MM-dd HH:mm:ss")}\n" +
                $"上次登录：{UsersViewModel.FmtTime(user.lastLogon, "yyyy-MM-dd HH:mm:ss")}",
        };

        // 1 列纵向流：开关/密码行各占一行（原横排）
        var propsPanel = new StackPanel();
        propsPanel.Children.Add(new TextBlock { Text = "用户名", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) });
        propsPanel.Children.Add(_nameBox);
        propsPanel.Children.Add(_hint);
        propsPanel.Children.Add(new TextBlock { Text = "全名", FontSize = 12, Margin = new Thickness(0, 6, 0, 2) });
        propsPanel.Children.Add(_fullNameBox);
        propsPanel.Children.Add(new TextBlock { Text = "描述", FontSize = 12, Margin = new Thickness(0, 6, 0, 2) });
        propsPanel.Children.Add(_descBox);
        propsPanel.Children.Add(_enabledChk);
        propsPanel.Children.Add(_mayChangeChk);
        propsPanel.Children.Add(_neverExpiresChk);
        propsPanel.Children.Add(_pwdSwitch);
        propsPanel.Children.Add(_newPwdBox);
        propsPanel.Children.Add(_pwdStrength);
        propsPanel.Children.Add(genPwdBtn);
        propsPanel.Children.Add(detail);
        propsPanel.Children.Add(saveBtn);

        // ===== 所属组 Tab =====
        _currentGroups = new ListBox { FontSize = 12, Height = 90 };
        _addGroupCombo = new ComboBox { FontSize = 13, Width = 300, Margin = new Thickness(0, 0, 0, 4) };
        var addGrpBtn = new Button { Content = "添加", MinWidth = 60, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        addGrpBtn.Click += async (_, _) =>
        {
            var g = (_addGroupCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (g is null) return;
            if (await _vm.AddGroupMemberAsync(g, _userName) is { ok: true })
            {
                Operated = true;
                await _vm.RefreshAsync();
                ReloadGroupViews();
            }
        };
        _removeCombo = new ComboBox { FontSize = 13, Width = 220, Margin = new Thickness(0, 0, 0, 4) };
        var removeBtn = new Button { Content = "移除", MinWidth = 60, HorizontalAlignment = HorizontalAlignment.Left };
        removeBtn.Click += async (_, _) =>
        {
            var g = _removeCombo.SelectedItem as string;
            if (g is null) return;
            if (await _vm.RemoveGroupMemberAsync(g, _userName) is { ok: true })
            {
                Operated = true;
                await _vm.RefreshAsync();
                ReloadGroupViews();
            }
        };
        var groupsPanel = new StackPanel();
        groupsPanel.Children.Add(new TextBlock { Text = "当前所属组", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        groupsPanel.Children.Add(_currentGroups);
        groupsPanel.Children.Add(new TextBlock { Text = "添加到组", FontSize = 12, Margin = new Thickness(0, 8, 0, 4) });
        // 1 列纵向流：下拉/按钮各占一行（原横排）
        groupsPanel.Children.Add(_addGroupCombo);
        groupsPanel.Children.Add(addGrpBtn);
        groupsPanel.Children.Add(new TextBlock { Text = "从组移除：", FontSize = 12, Margin = new Thickness(0, 8, 0, 4) });
        groupsPanel.Children.Add(_removeCombo);
        groupsPanel.Children.Add(removeBtn);

        // ===== 共享权限 Tab =====
        _permGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            FontSize = 12,
            Height = 160,
        };
        _permGrid.Columns.Add(new DataGridTextColumn { Header = "共享名", Binding = new Binding("perm.shareName"), Width = 180 });
        _permGrid.Columns.Add(new DataGridTextColumn { Header = "访问级别", Binding = new Binding("AccessText"), Width = 100 });
        _permGrid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding("TypeText"), Width = 80 });
        var permRefresh = new Button { Content = "刷新", MinWidth = 60 };
        permRefresh.Click += async (_, _) =>
            _permGrid.ItemsSource = await _vm.LoadUserSharePermsAsync(_userName);
        var permsPanel = new StackPanel();
        permsPanel.Children.Add(new TextBlock
        {
            Text = "该用户在各共享上的访问权限（点击「刷新」加载）",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        });
        // 1 列纵向流：刷新按钮与说明文字各占一行（原横排）
        permsPanel.Children.Add(_permGrid);
        permRefresh.HorizontalAlignment = HorizontalAlignment.Left;
        permRefresh.Margin = new Thickness(0, 8, 0, 0);
        permsPanel.Children.Add(permRefresh);
        permsPanel.Children.Add(new TextBlock
        {
            Text = "权限来源为 SMB 共享级别授权。NTFS 级别权限请在「共享管理」页对单个共享编辑。",
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        // 1 列纵向流：区块选择器纵向堆叠，正文区自上而下全展开、选中区块可见（原 TabControl 页签隐藏结构取消）
        var rbProps = new RadioButton { Content = "属性", GroupName = "UserDetailTab", IsChecked = true, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
        var rbGroups = new RadioButton { Content = "所属组", GroupName = "UserDetailTab", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
        var rbPerms = new RadioButton { Content = "共享权限", GroupName = "UserDetailTab", FontSize = 13 };
        void ApplySectionVisibility()
        {
            propsPanel.Visibility = rbProps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            groupsPanel.Visibility = rbGroups.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            permsPanel.Visibility = rbPerms.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        rbProps.Checked += (_, _) => ApplySectionVisibility();
        rbGroups.Checked += (_, _) => ApplySectionVisibility();
        rbPerms.Checked += (_, _) => ApplySectionVisibility();

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(rbProps);
        root.Children.Add(rbGroups);
        root.Children.Add(rbPerms);
        root.Children.Add(new StackPanel { Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(propsPanel);
        root.Children.Add(groupsPanel);
        root.Children.Add(permsPanel);
        Content = root;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ReloadGroupViews();

        void ReloadGroupViews()
        {
            var userNow = _vm.Users.FirstOrDefault(u => u.name == _userName) ?? _user;
            _currentGroups.Items.Clear();
            _currentGroups.Items.Add(userNow.groups.Count == 0 ? "未加入任何组" : string.Join("、", userNow.groups));
            _removeCombo.Items.Clear();
            foreach (var g in userNow.groups)
                _removeCombo.Items.Add(g);

            var inGroups = new HashSet<string>(userNow.groups);
            _addGroupCombo.Items.Clear();
            foreach (var g in _vm.Groups.Where(g => !inGroups.Contains(g.name)))
                _addGroupCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{g.name}{(string.IsNullOrEmpty(g.description) ? "" : $" ({g.description})")}",
                    Tag = g.name,
                });
            _addGroupCombo.IsEnabled = _addGroupCombo.Items.Count > 0;
            // 对齐 TS：availableGroups 空但 groups 非空 → 提示
        }
    }

    void UpdateStrength()
    {
        var s = PasswordGen.Evaluate(_newPwdBox.Text);
        _pwdStrength.Text = s switch
        {
            "weak" => "弱",
            "medium" => "中",
            "strong" => "强",
            _ => "",
        };
        _pwdStrength.Foreground = s switch
        {
            "weak" => Brushes.Red,
            "medium" => Brushes.Orange,
            "strong" => Brushes.Green,
            _ => Brushes.Gray,
        };
    }
}

/// <summary>
/// 密码生成/强度（对齐 src/utils/password.ts generatePassword/evaluateStrength：
/// 12 位、四类字符集各至少一个、洗牌；强度=长度+字符种类多样性）。
/// </summary>
public static class PasswordGen
{
    const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    const string Lower = "abcdefghijklmnopqrstuvwxyz";
    const string Digits = "0123456789";
    const string Special = "!@#$%^&*()-_=+[]{}";

    static readonly Random Rng = new();

    static char Rand(string pool) => pool[Rng.Next(pool.Length)];

    /// <summary>生成随机强密码（对齐 TS generatePassword(12)）。</summary>
    public static string Generate(int length = 12)
    {
        var chars = new List<char> { Rand(Upper), Rand(Lower), Rand(Digits), Rand(Special) };
        var all = Upper + Lower + Digits + Special;
        while (chars.Count < length) chars.Add(Rand(all));
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return string.Concat(chars);
    }

    /// <summary>强度（对齐 evaluateStrength：&lt;6 或低多样性=weak；≥8 且 ≥2 类=medium；≥12 且 ≥3 类=strong）。</summary>
    public static string? Evaluate(string? pwd)
    {
        if (pwd is null) return null;
        if (pwd.Length < 6) return "weak";
        int variety = 0;
        if (pwd.Any(char.IsLower)) variety++;
        if (pwd.Any(char.IsUpper)) variety++;
        if (pwd.Any(char.IsDigit)) variety++;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) variety++;
        if (pwd.Length >= 12 && variety >= 3) return "strong";
        if (pwd.Length >= 8 && variety >= 2) return "medium";
        return "weak";
    }
}
