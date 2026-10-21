// 移植自 electron/services/transfer.ts（设置整体导入导出：搬的是应用偏好与运维规则，不是业务数据）。
// 刻意不导出 dashboardHistory 与 journal；导入不信任文件——形状校验 → 逐字段类型收敛 → 交给 stateStore。
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Misc;

/// <summary>
/// ITransferService 实现。经 <see cref="IStateStore"/> 注入状态存取（TS 测试以假 stateStore 替身同法）。
/// </summary>
public sealed class TransferService : ITransferService
{
    private readonly IStateStore _store;

    public TransferService(IStateStore store) => _store = store;

    /// <summary>TS: exportSettings()——字段显式列出，不 spread 整份 AppState。</summary>
    public string ExportSettings()
    {
        var s = _store.LoadState();
        var bundle = new SettingsBundle
        {
            format = Transfer.Format,
            version = Transfer.Version,
            exportedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            settings = new SettingsPayload
            {
                theme = s.theme,
                advancedMode = s.advancedMode,
                pinned = s.pinned.ToList(),
                alertRules = new AlertRules
                {
                    idleAlertMinutes = s.alertRules.idleAlertMinutes,
                    smb1Alert = s.alertRules.smb1Alert,
                    weakPasswordAlert = s.alertRules.weakPasswordAlert,
                    diskLowGb = s.alertRules.diskLowGb,
                },
                autoStart = s.autoStart,
            },
        };
        // TS: JSON.stringify(bundle, null, 2)
        return JsonSerializer.Serialize(bundle, BundleOptions);
    }

    private static readonly JsonSerializerOptions BundleOptions = JsonCompat.IndentedWithEnums;

    /// <summary>
    /// TS: importSettings(json)——护栏（先拒超大再 parse、格式/版本/结构逐层校验），
    /// 语义是「合法字段应用、非法字段忽略」，而不是整体拒绝或整体写入。
    /// </summary>
    public SettingsImportResult ImportSettings(string json)
    {
        if (json is null) throw Errors.InvalidParam("导入内容必须是文本");
        if (json.Length > Transfer.MaxJsonBytes)
        {
            throw Errors.InvalidParam($"导入内容过大（上限 {Transfer.MaxJsonBytes / 1024} KB）");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Errors.InvalidParam("不是合法的 JSON 文件");
        }
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw Errors.InvalidParam("文件结构不是本应用的设置导出");
        }

        if (root.TryGetProperty("format", out var fmt)
                ? (fmt.ValueKind != JsonValueKind.String || fmt.GetString() != Transfer.Format)
                : true)
        {
            throw Errors.InvalidParam("文件格式不符：不是本应用的设置导出文件");
        }
        if (!root.TryGetProperty("version", out var ver) || ver.ValueKind is not JsonValueKind.Number)
        {
            throw Errors.InvalidParam("文件缺少版本号，无法判断兼容性");
        }
        double version = ver.GetDouble();
        if (version > Transfer.Version)
        {
            throw Errors.InvalidParam(
                $"文件版本（{version.ToString(CultureInfo.InvariantCulture)}）高于本程序支持的版本（{Transfer.Version}），请先升级程序");
        }

        if (!root.TryGetProperty("settings", out var raw) || raw.ValueKind is not JsonValueKind.Object)
        {
            throw Errors.InvalidParam("文件里没有 settings 段");
        }

        var current = _store.LoadState();
        var applied = new List<string>();
        var patch = new AppStatePatch();

        if (raw.TryGetProperty("theme", out var themeEl) && themeEl.ValueKind == JsonValueKind.String)
        {
            string theme = themeEl.GetString()!;
            if (theme == "light" || theme == "dark")
            {
                patch = patch with { theme = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light };
                applied.Add("theme");
            }
        }
        if (raw.TryGetProperty("advancedMode", out var amEl) && amEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            patch = patch with { advancedMode = amEl.GetBoolean() };
            applied.Add("advancedMode");
        }
        if (raw.TryGetProperty("pinned", out var pinEl) && pinEl.ValueKind == JsonValueKind.Array)
        {
            var pinned = pinEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
            if (pinned.Count > 0)
            {
                patch = patch with { pinned = pinned };
                applied.Add("pinned");
            }
        }
        if (raw.TryGetProperty("alertRules", out var arEl) && arEl.ValueKind == JsonValueKind.Object)
        {
            // 逐键收敛交给 stateStore.SaveState：这里只拒绝"根本不是对象"的形态
            patch = patch with { alertRules = MergeAlertRulesFromJson(current.alertRules, arEl) };
            applied.Add("alertRules");
        }
        if (raw.TryGetProperty("autoStart", out var asEl) && asEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            patch = patch with { autoStart = asEl.GetBoolean() };
            applied.Add("autoStart");
        }

        if (applied.Count == 0) throw Errors.InvalidParam("文件里没有任何可识别的设置项");

        _store.SaveState(patch);
        return new SettingsImportResult { applied = applied };
    }

    /// <summary>
    /// 逐键收敛（合法键应用、非法/缺失键回落当前值——对应 TS 交给 stateStore.normalize 的语义，
    /// 但 C# AlertRules patch 不可表达部分键，故在 transfer 层直接收敛出完整对象）。
    /// </summary>
    private static AlertRules MergeAlertRulesFromJson(AlertRules current, JsonElement arEl)
    {
        double? idle = NumberProp(arEl, "idleAlertMinutes");
        double? disk = NumberProp(arEl, "diskLowGb");
        return new AlertRules
        {
            idleAlertMinutes = idle is { } v && v > 0 ? (int)System.Math.Floor(v + 0.5) : current.idleAlertMinutes,
            smb1Alert = BoolProp(arEl, "smb1Alert", current.smb1Alert),
            weakPasswordAlert = BoolProp(arEl, "weakPasswordAlert", current.weakPasswordAlert),
            diskLowGb = disk is { } dv && dv >= 0 ? (int)System.Math.Floor(dv + 0.5) : current.diskLowGb,
        };
    }

    private static double? NumberProp(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool BoolProp(JsonElement parent, string key, bool fallback) =>
        parent.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
