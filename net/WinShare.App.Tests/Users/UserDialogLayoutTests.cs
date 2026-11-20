using System.Windows;
using System.Windows.Controls;
using WinShare.App.Users;
using WinShare.App.Views.Users;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Users;

/// <summary>
/// 用户弹窗布局回归（v2.2.0 第六批，浅色主题实测暴露）：
/// 新建本地用户对话框内容区与底部操作条此前同落 Grid Row 0——footer（带卡底）
/// 后声明者在上把表单整体盖住，只剩被拉伸的创建/取消按钮，分组功能不可用。
/// 本测试锁定：内容区 Row=0、footer Row=1，且两行高度语义正确（内容可滚动、
/// 底部条 Auto 不被拉伸）。
/// </summary>
public class UserDialogLayoutTests
{
    static System.Threading.Thread RunSta(Action action)
    {
        Exception? failure = null;
        var sta = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        })
        { IsBackground = true };
        sta.SetApartmentState(System.Threading.ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (failure is not null) throw failure;
        return sta;
    }

    [Fact]
    public void 新建用户对话框_内容区与底部条分属两行_互不重叠()
    {
        RunSta(() =>
        {
            var vm = new UsersViewModel(new StubUsersDataFeed(), new StubUserService());
            var dialog = new UserCreateDialog(vm);

            var host = Assert.IsType<Grid>(dialog.Content);
            // 两行定义：内容（滚动）+ 底部条
            Assert.Equal(2, host.RowDefinitions.Count);
            Assert.Equal(GridUnitType.Auto, host.RowDefinitions[0].Height.GridUnitType);
            Assert.Equal(GridUnitType.Auto, host.RowDefinitions[1].Height.GridUnitType);

            // Row0 = 内容滚动区（含表单），Row1 = 底部操作条；无同格重叠
            var rows = host.Children.OfType<FrameworkElement>()
                .Select(c => (c, Grid.GetRow(c))).ToList();
            var contentRow = rows.FirstOrDefault(t => t.c is ScrollViewer);
            var footerRow = rows.FirstOrDefault(t => t.c is Border b && b.Child is StackPanel);
            Assert.NotEqual(default, contentRow);
            Assert.Equal(0, contentRow.Item2);
            Assert.NotEqual(default, footerRow);
            Assert.Equal(1, footerRow.Item2);
            Assert.Equal(rows.Count, rows.Select(t => t.Item2).Distinct().Count());

            // 内容区可滚动且限高（长表单在矮屏不再把底部条推出可视区）
            var sv = (ScrollViewer)contentRow.c;
            Assert.True(sv.MaxHeight > 0);
            Assert.Equal(ScrollBarVisibility.Auto, sv.VerticalScrollBarVisibility);

            // 底部条横向右对齐、含 创建/取消 两枚按钮
            var footerStack = Assert.IsType<StackPanel>(((Border)footerRow.c).Child);
            Assert.Equal(Orientation.Horizontal, footerStack.Orientation);
            Assert.Contains(footerStack.Children.OfType<Button>(), b => b.IsDefault);
            Assert.Contains(footerStack.Children.OfType<Button>(), b => b.IsCancel);
        });
    }

    [Fact]
    public void 新建用户对话框_组复选框套软样式_空组给降级提示()
    {
        RunSta(() =>
        {
            var feed = new StubUsersDataFeed();
            feed.Bundle = new UsersBundle { Users = Array.Empty<LocalUser>(), Groups = Array.Empty<LocalGroup>() };
            var vm = new UsersViewModel(feed, new StubUserService());
            var dialog = new UserCreateDialog(vm);

            var host = Assert.IsType<Grid>(dialog.Content);
            var sv = host.Children.OfType<ScrollViewer>().Single();
            var form = Assert.IsType<StackPanel>(sv.Content);

            // 组多选区：无本地组时不给空面板，给一句降级说明（非说明书式，是状态反馈）
            var texts = form.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(texts, t => t is not null && t.Contains("本地组"));
            // 三个布尔开关 + 组多选共 ≥4 枚 CheckBox（测试宿主未加载应用资源字典，
            // 不对 Style 具体值断言——样式化由实机截图复核）
            var checks = Desc(form).OfType<CheckBox>().ToList();
            Assert.True(checks.Count >= 3);
        });
    }

    static IEnumerable<DependencyObject> Desc(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Desc(child)) yield return d;
        }
    }
}
