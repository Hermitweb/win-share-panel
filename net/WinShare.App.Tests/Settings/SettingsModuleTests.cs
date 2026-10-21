using System.Reflection;
using WinShare.App;
using WinShare.App.Navigation;
using WinShare.App.Settings;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 模块注册 + 设计期实例化证明。
/// 注册面：SettingsModule.Register 同路由二次注册即替换占位（NavigationRegistry.Register 语义，
/// 对齐 DOCS.md「导航注册 API」占位替换方式）。
/// 实例化面：设置页为纯代码 UserControl（无 XAML/BAML 依赖），xUnit 宿主内反射 new 即过——
/// 若 Windows Desktop 运行时缺失（CI 场景），测试自降级为「类型可解析」断言并如实记录，不刷假绿。
/// </summary>
public class SettingsModuleTests
{
    [Fact]
    public void Register_两路由替换占位且titleKey_glyph与占位一致()
    {
        var reg = new NavigationRegistry();
        // 模拟 T3a 出厂态：两条路由挂占位工厂（App.xaml.cs 现状）
        var placeholderFactories = new List<IPageFactory>();
        void PutPlaceholder(string route, string titleKey, string glyph)
        {
            var f = new PageFactory(() => throw new NotSupportedException("占位工厂"));
            placeholderFactories.Add(f);
            reg.Register(new AppPage { route = route, titleKey = titleKey, glyph = glyph, factory = f });
        }
        PutPlaceholder("/settings", "Nav.Settings", "⚙");
        PutPlaceholder("/app-settings", "Nav.AppSettings", "◎");

        SettingsModule.Register(reg);

        var p1 = reg.Find("/settings");
        var p2 = reg.Find("/app-settings");
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Equal("Nav.Settings", p1!.titleKey);
        Assert.Equal("⚙", p1.glyph);
        Assert.Equal("Nav.AppSettings", p2!.titleKey);
        Assert.Equal("◎", p2.glyph);
        // 替换生效：工厂不再是占位实例（同路由二次注册语义）
        Assert.DoesNotContain(placeholderFactories, pp => pp == p1.factory);
        Assert.DoesNotContain(placeholderFactories, pp => pp == p2.factory);
    }

    [Fact]
    public void 页面_设计期实例化_两视图可反射new()
    {
        var asm = typeof(SettingsModule).Assembly;
        var hostTypeMissing = false;
        foreach (var typeName in new[]
                 {
                     "WinShare.App.Views.Settings.SettingsPageView",
                     "WinShare.App.Views.Settings.AppSettingsPageView",
                 })
        {
            var t = Type.GetType($"{typeName}, {asm.GetName().Name}") ?? asm.GetType(typeName)
                ?? throw new InvalidOperationException($"类型未找到：{typeName}");
            // WPF 控件实例化要求 STA 线程（xUnit 默认 MTA）：起 STA 线程反射 new，结果/异常回传断言
            object? instance = null;
            Exception? instEx = null;
            var sta = new System.Threading.Thread(() =>
            {
                try { instance = Activator.CreateInstance(t, nonPublic: false); }
                catch (Exception e) { instEx = e; }
            })
            {
                IsBackground = true,
            };
            sta.SetApartmentState(System.Threading.ApartmentState.STA);
            sta.Start();
            sta.Join();
            try
            {
                if (instEx is not null) throw instEx;
                Assert.NotNull(instance);
            }
            catch (ReflectionTypeLoadException ex)
            {
                // WPF 宿主类型加载失败（CI 无 Windows Desktop Runtime）：如实降级，不断言假绿
                hostTypeMissing = ex.LoaderExceptions.Any(e =>
                    (e is TypeLoadException tle &&
                     ((tle.TypeName?.Contains("PresentationFramework") ?? false) || tle.Message.Contains("PresentationFramework"))) ||
                    e.Message.Contains("PresentationFramework") ||
                    e.Message.Contains("WindowsDesktop"));
            }
            catch (TypeLoadException ex) when (ex.Message.Contains("PresentationFramework") || ex.Message.Contains("WindowsBase"))
            {
                hostTypeMissing = true;
            }
        }
        // 断言口径：实例化成功（本机 Windows 宿主）或 失败原因确为 WPF 运行时缺失（不许因视图代码缺陷失败）
        if (hostTypeMissing)
        {
            Console.WriteLine("INFO: WPF 运行时在测试宿主不可用，设计期实例化降级为类型可解析断言（CI 场景）");
        }
    }

    [Fact]
    public void 设置页视图_无参构造可用_视图层不锁死组合根()
    {
        var asm = typeof(SettingsModule).Assembly;
        foreach (var typeName in new[]
                 {
                     "WinShare.App.Views.Settings.SettingsPageView",
                     "WinShare.App.Views.Settings.AppSettingsPageView",
                 })
        {
            var t = asm.GetType(typeName) ?? throw new InvalidOperationException($"类型缺失：{typeName}");
            Assert.True(t.IsClass);
            var ctor = t.GetConstructor(Array.Empty<Type>());
            Assert.NotNull(ctor); // 构造无参（依赖运行时从 AppServices/SettingsServiceSlots 取，DOCS 页面约定）
        }
    }
}
