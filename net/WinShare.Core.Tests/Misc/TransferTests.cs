// 移植自 electron/services/transfer.test.ts（TS: transfer.ts → C#: TransferService）。
// TS 以 vi.mock('../lib/stateStore') 假 store；C# 经 IStateStore 注入等价替身。
// 适配标注：
//  - 契约 ITransferService.ImportSettings(string json) 有类型边界 → TS '非字符串' 场景无 C# 等价（标注 dropped）；
//  - alertRules 收敛结果按 C# 完整 AlertRules 断言（transfer 层已逐键收敛出完整对象）。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Misc;

namespace WinShare.Core.Tests.Misc;

public class TransferTests
{
    // TS store mock 等价：记录 patch，不落盘
    private sealed class FakeStore : IStateStore
    {
        public AppState State = null!;
        public List<AppStatePatch> Saved { get; } = new();

        public AppState LoadState() => State;
        public AppState SaveState(AppStatePatch patch)
        {
            Saved.Add(patch);
            return State;
        }
        public void PushHistory(HistoryPoint point) { }
        public JournalEntry AddJournal(JournalEntryDraft draft) => throw new NotSupportedException();
        public void RemoveJournal(string id) { }
        public void ResetStateCacheForTesting() { }
    }

    // TS CURRENT（含运行时数据：必须**不**出现在导出里）
    private static AppState Current() => new()
    {
        theme = ThemeMode.Dark,
        advancedMode = false,
        pinned = new[] { "smb:Reports", "ftp:pub" },
        alertRules = new AlertRules { idleAlertMinutes = 30, smb1Alert = false, weakPasswordAlert = true, diskLowGb = 5 },
        autoStart = true,
        dashboardHistory = new[] { new HistoryPoint { ts = 1, sessions = 2, openFiles = 0 } },
        journal = new[]
        {
            new JournalEntry { id = "j1", action = JournalAction.Delete, protocol = Protocol.Smb, name = "x" },
        },
    };

    private readonly FakeStore _store = new();
    private readonly TransferService _svc;

    public TransferTests()
    {
        _store.State = Current();
        _svc = new TransferService(_store);
    }

    private static string Bundle(object settings, int version = Transfer.Version, string format = Transfer.Format) =>
        JsonSerializer.Serialize(new { format, version, exportedAt = "x", settings });

    private static JsonElement Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ===== exportSettings =====

    // [Source: transfer.test.ts#exportSettings > 形状：format / version / exportedAt / settings 五项一一对应]
    [Fact]
    public void Export_Shape()
    {
        JsonElement b = Parse(_svc.ExportSettings());
        Assert.Equal(Transfer.Format, b.GetProperty("format").GetString());
        Assert.Equal(Transfer.Version, b.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.String, b.GetProperty("exportedAt").ValueKind);
        JsonElement settings = b.GetProperty("settings");
        Assert.Equal("dark", settings.GetProperty("theme").GetString());
        Assert.False(settings.GetProperty("advancedMode").GetBoolean());
        Assert.Equal(new[] { "smb:Reports", "ftp:pub" }, settings.GetProperty("pinned").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(30, settings.GetProperty("alertRules").GetProperty("idleAlertMinutes").GetInt32());
        Assert.False(settings.GetProperty("alertRules").GetProperty("smb1Alert").GetBoolean());
        Assert.True(settings.GetProperty("alertRules").GetProperty("weakPasswordAlert").GetBoolean());
        Assert.Equal(5, settings.GetProperty("alertRules").GetProperty("diskLowGb").GetInt32());
        Assert.True(settings.GetProperty("autoStart").GetBoolean());
    }

    // [Source: transfer.test.ts#exportSettings > 关键不变式：不导出 dashboardHistory / journal（运行时数据不可当偏好搬家）]
    [Fact]
    public void Export_ExcludesRuntimeData()
    {
        string text = _svc.ExportSettings();
        Assert.DoesNotContain("dashboardHistory", text);
        Assert.DoesNotContain("journal", text);
        // 反向钉：连字段名都不该出现，更不该带值
        Assert.DoesNotContain("\"j1\"", text);
    }

    // [Source: transfer.test.ts#exportSettings > 导出的是快照，不是引用：外部改动 store 不会污染已导出文本]
    [Fact]
    public void Export_IsSnapshotNotReference()
    {
        string text = _svc.ExportSettings();
        _store.State = _store.State with { pinned = new[] { "changed" } };
        Assert.Equal(new[] { "smb:Reports", "ftp:pub" }, Parse(text).GetProperty("settings").GetProperty("pinned")
            .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ===== importSettings：护栏 =====

    // [Source: transfer.test.ts#importSettings：护栏 > 非字符串 / 超大 / 非 JSON]
    // 说明：'非字符串' 场景由契约 ImportSettings(string) 类型边界覆盖，无 C# 等价用例（dropped）；其余两条全量移植。
    [Fact]
    public void Import_GuardsOversizeAndBadJson()
    {
        AppException tooBig = Assert.Throws<AppException>(() => _svc.ImportSettings(new string('x', Transfer.MaxJsonBytes + 1)));
        Assert.Contains("过大", tooBig.Message);
        AppException badJson = Assert.Throws<AppException>(() => _svc.ImportSettings("{not json"));
        Assert.Contains("不是合法的 JSON", badJson.Message);
        Assert.Equal("INVALID_PARAM", badJson.Code);
    }

    // [Source: transfer.test.ts#importSettings：护栏 > 格式不符（含别的应用的 JSON）]
    [Fact]
    public void Import_FormatMismatch()
    {
        AppException otherApp = Assert.Throws<AppException>(
            () => _svc.ImportSettings(JsonSerializer.Serialize(new { hello = "world" })));
        Assert.Contains("格式不符", otherApp.Message);

        AppException namedMismatch = Assert.Throws<AppException>(
            () => _svc.ImportSettings(Bundle(new { theme = "dark" }, format: "other-app/settings")));
        Assert.Contains("格式不符", namedMismatch.Message);
    }

    // [Source: transfer.test.ts#importSettings：护栏 > 版本高于本程序：明确拒绝并给出可执行建议，不尝试猜]
    [Fact]
    public void Import_VersionNewerRejected()
    {
        AppException ex = Assert.Throws<AppException>(
            () => _svc.ImportSettings(Bundle(new { theme = "dark" }, version: Transfer.Version + 1)));
        Assert.Contains("高于本程序支持的版本", ex.Message);
    }

    // [Source: transfer.test.ts#importSettings：护栏 > 缺版本号 / 缺 settings 段]
    [Fact]
    public void Import_MissingVersionOrSettings()
    {
        AppException noVersion = Assert.Throws<AppException>(
            () => _svc.ImportSettings(JsonSerializer.Serialize(new { format = Transfer.Format, settings = new { } })));
        Assert.Contains("缺少版本号", noVersion.Message);

        // TS bundle(undefined)：JSON.stringify 丢弃 undefined → 等价于无 settings 键
        AppException noSettings = Assert.Throws<AppException>(
            () => _svc.ImportSettings(JsonSerializer.Serialize(new { format = Transfer.Format, version = 1, exportedAt = "x" })));
        Assert.Contains("没有 settings 段", noSettings.Message);
    }

    // [Source: transfer.test.ts#importSettings：护栏 > 解析后没有任何可识别设置项：拒绝而不是静默"成功"]
    [Fact]
    public void Import_NoRecognizableFieldsRejected()
    {
        AppException unknown = Assert.Throws<AppException>(
            () => _svc.ImportSettings(Bundle(new { unknownField = 1 })));
        Assert.Contains("没有任何可识别的设置项", unknown.Message);

        AppException dirtyAll = Assert.Throws<AppException>(
            () => _svc.ImportSettings(Bundle(new { theme = "blue", advancedMode = "yes" })));
        Assert.Contains("没有任何可识别的设置项", dirtyAll.Message);
    }

    // ===== importSettings：逐字段收敛（合法应用、非法忽略）=====

    // [Source: transfer.test.ts#importSettings：逐字段收敛 > 完整文件：apply 全部五项，并把 patch 交给 saveState]
    [Fact]
    public void Import_FullFileAppliesAllFive()
    {
        _store.Saved.Clear();
        SettingsImportResult r = _svc.ImportSettings(_svc.ExportSettings());
        Assert.Equal(new[] { "theme", "advancedMode", "pinned", "alertRules", "autoStart" }, r.applied.ToArray());
        Assert.Single(_store.Saved);
        AppStatePatch p = _store.Saved[0];
        Assert.Equal(ThemeMode.Dark, p.theme);
        Assert.Equal(false, p.advancedMode);
        Assert.Equal(new[] { "smb:Reports", "ftp:pub" }, p.pinned?.ToArray());
        Assert.NotNull(p.alertRules);
        Assert.Equal(30, p.alertRules!.idleAlertMinutes);
        Assert.False(p.alertRules.smb1Alert);
        Assert.True(p.alertRules.weakPasswordAlert);
        Assert.Equal(5, p.alertRules.diskLowGb);
        Assert.Equal(true, p.autoStart);
    }

    // [Source: transfer.test.ts#importSettings：逐字段收敛 > 往返幂等：export → import 得到的 patch 与当前设置一致]
    [Fact]
    public void Import_RoundTripIdempotent()
    {
        SettingsImportResult r = _svc.ImportSettings(_svc.ExportSettings());
        Assert.Equal(5, r.applied.Count);
        AppStatePatch p = _store.Saved[0];
        Assert.Equal(ThemeMode.Dark, p.theme);
        Assert.Equal(false, p.advancedMode);
        Assert.Equal(true, p.autoStart);
    }

    // [Source: transfer.test.ts#importSettings：逐字段收敛 > 混合脏值：只应用合法字段，不因个别脏值整体拒绝]
    [Fact]
    public void Import_MixedDirtyValuesOnlyLegalApplied()
    {
        _store.Saved.Clear();
        SettingsImportResult r = _svc.ImportSettings(Bundle(new
        {
            theme = "blue",
            advancedMode = true,
            autoStart = "yes",
            pinned = new object[] { "ok", 7, null },
        }));
        Assert.Equal(new[] { "advancedMode", "pinned" }, r.applied.ToArray());
        AppStatePatch p = _store.Saved[0];
        Assert.Equal(true, p.advancedMode);
        Assert.Equal(new[] { "ok" }, p.pinned?.ToArray());
        Assert.Null(p.theme);
        Assert.Null(p.alertRules);
        Assert.Null(p.autoStart);
    }

    // [Source: transfer.test.ts#importSettings：逐字段收敛 > 字符串自动启 —— stateStore.normalize 会收敛，transfer 只做形状层放行]
    // 说明：TS 用例正文传 autoStart:true（布尔），按正文移植。
    [Fact]
    public void Import_AutoStartAppliedWhenBoolean()
    {
        _store.Saved.Clear();
        SettingsImportResult r = _svc.ImportSettings(Bundle(new { autoStart = true }));
        Assert.Equal(new[] { "autoStart" }, r.applied.ToArray());
        Assert.Equal(true, _store.Saved[0].autoStart);
    }
}
