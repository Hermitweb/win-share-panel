// 移植自 src/components/FirstRunGuide.tsx（首启三问向导：选文件夹 → 给谁用 → 完成）。
// 对新手的全部承诺落在结构上：
// - 只有三步，标题来自 FirstRunLogic.GuideSteps（与逻辑同源）；
// - SMB1/枚举/租约等术语只出现在折叠的「高级参数」里，不点开就不显示；
// - 默认路径来自 IDiskService.SuggestRootAsync（非系统盘余量最大卷）；
// - 权限三档人话，落参由 FirstRunLogic.TierToAccess 单点映射；
// - 创建成功写一次性完成标记，老用户绝不再被打扰；失败只在原地提示，不清空已填内容。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShare.App.Settings;
using WinShare.App.Views.Settings;
using WinShare.Core.Contracts;

namespace WinShare.App.FirstRun;

/// <summary>首启三问向导窗口。</summary>
public sealed class FirstRunGuideWindow : Window
{
    private readonly IShareService? _share;
    private readonly IDiskService? _disk;
    private readonly FirstRunFlagStore _flagStore;

    private readonly StackPanel _body = new();
    private readonly TextBlock _stepTitle = new();
    private readonly TextBlock _stepHint = new();
    private readonly TextBlock _errorText = new();
    private readonly Button _next;
    private readonly Button _back;
    private readonly Button _later;

    // 步骤一：路径 + 共享名
    private readonly TextBox _pathBox = new();
    private readonly TextBox _nameBox = new();
    // 步骤二：档位
    private readonly List<RadioButton> _tiers = new();
    // 步骤三：确认 + 高级
    private readonly TextBlock _summary = new();
    private readonly TextBox _limitBox = new();
    private readonly CheckBox _advancedToggle = new();
    private readonly StackPanel _advancedPanel = new();

    private int _step;
    private bool _nameEdited;

    public FirstRunGuideWindow(IShareService? share, IDiskService? disk, FirstRunFlagStore flagStore)
    {
        _share = share;
        _disk = disk;
        _flagStore = flagStore;

        Title = "欢迎用 WinShare，三步建好第一个共享";
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = SettingsUi.Token("BrushBg", "#F4FAFD");

        var root = new DockPanel { Margin = new Thickness(20) };

        // 顶部：标题 + 步骤提示
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock
        {
            Text = "欢迎用 WinShare，三步建好第一个共享",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
        });
        head.Children.Add(_stepTitle);
        head.Children.Add(_stepHint);
        _stepTitle.FontSize = 13;
        _stepTitle.Margin = new Thickness(0, 6, 0, 0);
        _stepTitle.Foreground = SettingsUi.Token("BrushPrimary", "#1677FF");
        _stepHint.FontSize = 12;
        _stepHint.Foreground = SettingsUi.Token("BrushFog", "#731F2733");
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // 底部：按钮行
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _back = SettingsUi.Button("上一步", (_, _) => SetStep(_step - 1));
        _next = SettingsUi.Button("下一步", (_, _) => SetStep(_step + 1), primary: true);
        _later = SettingsUi.Button("以后再说", (_, _) => Close());
        actions.Children.Add(_later);
        actions.Children.Add(_back);
        actions.Children.Add(_next);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        // 中部：步骤内容
        _errorText.FontSize = 12;
        _errorText.Margin = new Thickness(0, 8, 0, 0);
        _errorText.TextWrapping = TextWrapping.Wrap;
        _errorText.Foreground = SettingsUi.Token("BrushDanger", "#FF4D4F");
        _errorText.Visibility = Visibility.Collapsed;

        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mid = new StackPanel();
        mid.Children.Add(scroll);
        mid.Children.Add(_errorText);
        root.Children.Add(mid);

        Content = root;

        BuildSteps();
        SetStep(0);
        Loaded += async (_, _) => await SuggestPathAsync();
    }

    private void BuildSteps()
    {
        // ===== 步骤一：选文件夹 =====
        _pathBox.Margin = new Thickness(0, 4, 0, 8);
        _pathBox.Padding = new Thickness(6);
        _pathBox.TextChanged += (_, _) =>
        {
            if (!_nameEdited) _nameBox.Text = ShareNameFromPath(_pathBox.Text);
        };
        _nameBox.Margin = new Thickness(0, 4, 0, 8);
        _nameBox.Padding = new Thickness(6);
        _nameBox.TextChanged += (_, _) =>
        {
            if (_nameBox.IsKeyboardFocusWithin) _nameEdited = true;
        };
        _stepOne = new StackPanel();
        _stepOne.Children.Add(Label("要共享的文件夹"));
        _stepOne.Children.Add(_pathBox);
        _stepOne.Children.Add(SettingsUi.Button("浏览…", (_, _) => PickFolder()));
        _stepOne.Children.Add(Label("共享名（默认取文件夹名）"));
        _stepOne.Children.Add(_nameBox);

        // ===== 步骤二：给谁用 =====
        _stepTwo = new StackPanel();
        for (int i = 0; i < FirstRunLogic.PermTiers.Count; i++)
        {
            PermTierOption opt = FirstRunLogic.PermTiers[i];
            var rb = new RadioButton
            {
                GroupName = "tier",
                IsChecked = opt.Tier == FirstRunLogic.DefaultPermTier,
                Margin = new Thickness(0, 6, 0, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = opt.Label,
                            FontSize = 13,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = SettingsUi.Token("BrushText", "#1F2733"),
                        },
                        new TextBlock
                        {
                            Text = opt.Desc,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
                        },
                    },
                },
            };
            _tiers.Add(rb);
            _stepTwo.Children.Add(rb);
        }

        // ===== 步骤三：完成 + 高级折叠 =====
        _stepThree = new StackPanel();
        _summary.FontSize = 12;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Foreground = SettingsUi.Token("BrushText", "#1F2733");
        _stepThree.Children.Add(_summary);

        _advancedToggle.Content = "高级参数（点开才生效，不改保持默认）";
        _advancedToggle.Margin = new Thickness(0, 12, 0, 6);
        _advancedToggle.FontSize = 12;
        _advancedPanel.Visibility = Visibility.Collapsed;
        _advancedToggle.Checked += (_, _) => _advancedPanel.Visibility = Visibility.Visible;
        _advancedToggle.Unchecked += (_, _) => _advancedPanel.Visibility = Visibility.Collapsed;

        _limitBox.Width = 120;
        _limitBox.Padding = new Thickness(4);
        _limitBox.Margin = new Thickness(0, 2, 0, 6);
        _advancedPanel.Children.Add(Label("并发连接上限（0 或留空=不限制）"));
        _advancedPanel.Children.Add(_limitBox);
        _stepThree.Children.Add(_advancedToggle);
        _stepThree.Children.Add(_advancedPanel);
    }

    private StackPanel _stepOne = new();
    private StackPanel _stepTwo = new();
    private StackPanel _stepThree = new();

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 2),
        Foreground = SettingsUi.Token("BrushFog", "#731F2733"),
    };

    /// <summary>TS: shareNameFromPath —— 取末级目录名；含非法字符时收敛。</summary>
    private static string ShareNameFromPath(string? path)
    {
        string p = (path ?? "").Trim().TrimEnd('\\', '/');
        if (p.Length == 0) return "";
        int idx = p.LastIndexOfAny(new[] { '\\', '/' });
        string leaf = idx >= 0 ? p[(idx + 1)..] : p;
        // 盘根（C:\）无末级目录 → 用盘符
        if (leaf.Length == 0 || leaf.EndsWith(':')) return p.Replace(":", "").Replace("\\", "");
        return new string(leaf.Where(c => !"\\/:*?\"<>|".Contains(c)).ToArray());
    }

    private async Task SuggestPathAsync()
    {
        if (_disk is null) return;
        try
        {
            string? suggested = await _disk.SuggestRootAsync();
            if (!string.IsNullOrWhiteSpace(suggested) && _pathBox.Text.Length == 0)
            {
                _pathBox.Text = suggested;
            }
        }
        catch
        {
            // 查盘失败留空，由用户自己选（路径为空会被「下一步」禁用挡住）
        }
    }

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _pathBox.Text = dlg.FolderName;
        }
    }

    private PermTier SelectedTier()
    {
        for (int i = 0; i < _tiers.Count; i++)
        {
            if (_tiers[i].IsChecked == true)
            {
                return FirstRunLogic.PermTiers[i].Tier;
            }
        }
        return FirstRunLogic.DefaultPermTier;
    }

    private void SetStep(int step)
    {
        _step = Math.Clamp(step, 0, FirstRunLogic.GuideSteps.Count - 1);
        GuideStep meta = FirstRunLogic.GuideSteps[_step];
        _stepTitle.Text = $"第 {_step + 1} 步：{meta.Title}";
        _stepHint.Text = meta.Hint;
        _errorText.Visibility = Visibility.Collapsed;

        _body.Children.Clear();
        switch (_step)
        {
            case 0:
                _body.Children.Add(_stepOne);
                break;
            case 1:
                _body.Children.Add(_stepTwo);
                break;
            default:
                _summary.Text =
                    $"文件夹：{_pathBox.Text}\n共享名：{_nameBox.Text}\n权限：{FirstRunLogic.PermTiers.First(t => t.Tier == SelectedTier()).Label}\n\n" +
                    "创建后可在「共享管理」里随时改权限或删除。";
                _body.Children.Add(_stepThree);
                break;
        }

        _back.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool last = _step == FirstRunLogic.GuideSteps.Count - 1;
        _next.Content = last ? "创建共享" : "下一步";
        _next.Click -= OnNextClick;
        _next.Click += OnNextClick;
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            if (string.IsNullOrWhiteSpace(_pathBox.Text))
            {
                ShowError("请先选择一个文件夹");
                return;
            }
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
            {
                ShowError("请填共享名（可默认取文件夹名）");
                return;
            }
        }
        if (_step < FirstRunLogic.GuideSteps.Count - 1)
        {
            SetStep(_step + 1);
            return;
        }
        await CreateAsync();
    }

    private async Task CreateAsync()
    {
        _next.IsEnabled = false;
        try
        {
            if (_share is null)
            {
                ShowError("共享服务未就绪，无法创建（请稍后重试或到「共享管理」手动创建）");
                return;
            }
            int? limit = int.TryParse(_limitBox.Text.Trim(), out int parsed) && parsed > 0 ? parsed : null;
            CreateShareOpts input = FirstRunLogic.BuildCreateInput(
                _nameBox.Text, _pathBox.Text, SelectedTier(),
                _advancedToggle.IsChecked == true ? new FirstRunLogic.AdvancedOpts(limit) : null);
            await _share.CreateShareAsync(input);
            _flagStore.MarkComplete();
            MessageBox.Show("第一个共享建好了，打开资源管理器就能用", "创建成功");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            // 只提示、不清空：改改就能重试，重填是最差的失败体验
            ShowError(ex.Message);
        }
        finally
        {
            _next.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }
}
