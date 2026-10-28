using System.Collections;
using System.IO;
using System.Resources;
using System.Windows.Media.Imaging;

namespace WinShare.App.Shell;

/// <summary>
/// 应用图标加载：统一使用仓库根 win-share-panel.png 派生的资源。
/// - resources/logo.png（WPF Resource 内嵌 .g.resources，键 resources/logo.png）→ 窗口标题栏图标
/// - resources/icon.ico（内嵌清单资源）→ 系统托盘图标
///   （System.Drawing.Icon 只接受 ICO，不接受 PNG —— 原 logo.png 同样会被拒）
/// 与 Electron 侧一致：标题栏用 logo.png，应用/安装包/托盘用 icon.ico。
/// </summary>
public static class AppIcon
{
    const string GResourcesManifestName = "WinShare.App.g.resources";
    const string LogoKey = "resources/logo.png";

    static byte[] ReadAll(Stream source)
    {
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>从 .g.resources 取内嵌 PNG（与 XAML 的 Source="Resources/logo.png" 同一份资源）。</summary>
    static byte[] LogoBytes()
    {
        using var blob = typeof(AppIcon).Assembly.GetManifestResourceStream(GResourcesManifestName);
        if (blob == null) return Array.Empty<byte>();
        // pack URI 只在运行的 WPF 应用上下文里可解析（单元测试宿主下 System.Uri 直接拒绝
        // pack:// 形式），这里改为直接读 .g.resources —— 与 WPF 内部读取的是同一份数据。
        using var reader = new ResourceReader(blob);
        foreach (DictionaryEntry entry in reader)
        {
            if (!string.Equals(entry.Key as string, LogoKey, StringComparison.OrdinalIgnoreCase)) continue;
            return entry.Value switch
            {
                Stream s => ReadAll(s),
                byte[] b => b,
                _ => Array.Empty<byte>(),
            };
        }
        return Array.Empty<byte>();
    }

    /// <summary>
    /// WPF 窗口标题栏图标。资源缺失时返回 null，WPF 自动回退到 exe 图标
    /// （csproj ApplicationIcon 指向同一套图标），不会出现空白标题栏。
    /// </summary>
    public static BitmapImage? WindowIcon()
    {
        var bytes = LogoBytes();
        if (bytes.Length == 0) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// WinForms 托盘图标（读内嵌 ICO）。资源缺失时返回 null，由 TrayController 回退 SystemIcons.Application。
    /// </summary>
    public static System.Drawing.Icon? TrayIcon()
    {
        var name = typeof(AppIcon).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Resources.icon.ico", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        var bytes = ReadAll(stream);
        if (bytes.Length == 0) return null;
        // Icon 内部持有流引用：静态字段保活，避免流被回收后图标失效
        _trayStream = new MemoryStream(bytes);
        return new System.Drawing.Icon(_trayStream);
    }

    static MemoryStream? _trayStream;
}
