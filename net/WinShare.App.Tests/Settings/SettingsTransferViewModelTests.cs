using System.Text.Json;
using WinShare.App.Settings;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Settings;

/// <summary>
/// 设置整体导入导出测试（边界 + 逐字段收敛 + 错误文案逐字对齐）。
/// [Source: electron/services/transfer.ts + src/components/SettingsTransferCard.tsx]
/// </summary>
public class SettingsTransferViewModelTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wsp-transfer-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 导出_只含五类偏好_不含运行时数据()
    {
        var store = new AppSettingsStore(TempDir());
        store.SaveState(new AppStatePatch
        {
            theme = ThemeMode.Dark,
            advancedMode = false,
            pinned = new[] { "smb:share1" },
            autoStart = true,
            alertRules = new AlertRules { idleAlertMinutes = 15, smb1Alert = true, weakPasswordAlert = false, diskLowGb = 30 },
        });
        store.PushHistory(new HistoryPoint { ts = 1, sessions = 2, openFiles = 3 });

        var vm = new SettingsTransferViewModel(store);
        var json = vm.Export();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(SettingsTransferViewModel.TransferFormat, root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var settings = root.GetProperty("settings");
        Assert.Equal("dark", settings.GetProperty("theme").GetString());
        Assert.False(settings.GetProperty("advancedMode").GetBoolean());
        Assert.Equal("smb:share1", settings.GetProperty("pinned")[0].GetString());
        Assert.Equal(30, settings.GetProperty("alertRules").GetProperty("diskLowGb").GetInt32());
        Assert.True(settings.GetProperty("autoStart").GetBoolean());
        // 刻意不导出的运行时数据
        Assert.False(root.TryGetProperty("dashboardHistory", out _));
        Assert.False(root.TryGetProperty("journal", out _));
    }

    [Fact]
    public void 导入_合法文件_应用字段清单()
    {
        var store = new AppSettingsStore(TempDir());
        var vm = new SettingsTransferViewModel(store);
        var json = """
        {
          "format": "winshare-panel/settings",
          "version": 1,
          "exportedAt": "2026-01-01T00:00:00Z",
          "settings": { "theme": "dark", "advancedMode": true, "pinned": ["smb:a", 42, "smb:b"], "autoStart": true,
                        "alertRules": { "diskLowGb": 44 } }
        }
        """;
        var applied = vm.Import(json);
        Assert.Contains("theme", applied);
        Assert.Contains("advancedMode", applied);
        Assert.Contains("pinned", applied);
        Assert.Contains("alertRules", applied);
        Assert.Contains("autoStart", applied);
        var s = store.Load();
        Assert.Equal(ThemeMode.Dark, s.theme);
        Assert.Equal(2, s.pinned.Count);          // 非字符串项被滤掉
        Assert.True(s.autoStart);
        // 缺失键补全（C# 整体替换契约缺口处理：import 侧与当前态合并）
        Assert.Equal(44, s.alertRules.diskLowGb);
        Assert.True(s.alertRules.smb1Alert);       // 默认态保持
    }

    [Fact]
    public void 导入_文件过大_拒收()
    {
        var vm = new SettingsTransferViewModel(new AppSettingsStore(TempDir()));
        var big = new string('a', 512 * 1024 + 1);
        Assert.Throws<ArgumentException>(() => vm.Import(big));
    }

    [Theory]
    [InlineData("这不是 JSON", "不是合法的 JSON 文件")]
    [InlineData("[1,2]", "文件结构不是本应用的设置导出")]
    [InlineData("""{"format":"other","version":1,"settings":{}}""", "文件格式不符：不是本应用的设置导出文件")]
    [InlineData("""{"version":99,"settings":{}}""", "文件缺少版本号，无法判断兼容性")]
    [InlineData("""{"format":"winshare-panel/settings","version":2,"settings":{}}""", "文件版本（2）高于本程序支持的版本（1），请先升级程序")]
    [InlineData("""{"format":"winshare-panel/settings","version":1}""", "文件里没有 settings 段")]
    [InlineData("""{"format":"winshare-panel/settings","version":1,"settings":{"whatever":1}}""", "文件里没有任何可识别的设置项")]
    public void 导入_各非法分支_文案对齐TS(string json, string expectedMessage)
    {
        var vm = new SettingsTransferViewModel(new AppSettingsStore(TempDir()));
        var ex = Assert.Throws<ArgumentException>(() => vm.Import(json));
        Assert.Equal(expectedMessage, ex.Message);
    }
}
