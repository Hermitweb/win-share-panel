using System.Text.Json;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 设置整体导入导出卡。
/// [Source: electron/services/transfer.ts + src/components/SettingsTransferCard.tsx]
/// 边界必须让用户看见：只含应用偏好与运维规则（主题/新手模式/置顶/告警规则/自启意图）；
/// 刻意不导出 dashboardHistory 与 journal（运行时数据搬走会误导，同 transfer.ts 注释）。
/// 导入不信任文件：形状校验 → 逐字段类型收敛 → 交 store.SaveState（合法字段应用、非法字段忽略）。
/// </summary>
public sealed class SettingsTransferViewModel : SettingsCardBase
{
    public const string TransferFormat = "winshare-panel/settings";
    public const int TransferVersion = 1;
    public const int MaxJsonBytes = 512 * 1024;

    readonly AppSettingsStore _store;

    public SettingsTransferViewModel(AppSettingsStore store)
    {
        _store = store;
        Raise();
    }

    /// <summary>导出（字段显式列出，不 spread 整份 AppState；JSON 与 TS 版逐字段兼容）。</summary>
    public string Export()
    {
        var s = _store.Load();
        var bundle = new Dictionary<string, object?>
        {
            ["format"] = TransferFormat,
            ["version"] = TransferVersion,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["settings"] = new Dictionary<string, object?>
            {
                ["theme"] = s.theme == ThemeMode.Dark ? "dark" : "light",
                ["advancedMode"] = s.advancedMode,
                ["pinned"] = s.pinned.ToList(),
                ["alertRules"] = new Dictionary<string, object?>
                {
                    ["idleAlertMinutes"] = s.alertRules.idleAlertMinutes,
                    ["smb1Alert"] = s.alertRules.smb1Alert,
                    ["weakPasswordAlert"] = s.alertRules.weakPasswordAlert,
                    ["diskLowGb"] = s.alertRules.diskLowGb,
                },
                ["autoStart"] = s.autoStart,
            },
        };
        return JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 导入（对齐 TS importSettings 全部分支与错误文案；成功返回 applied 字段清单，
    /// 失败抛 <see cref="ArgumentException"/>，消息与 TS 逐字对齐）。
    /// </summary>
    public IReadOnlyList<string> Import(string json)
    {
        if (json.Length > MaxJsonBytes)
        {
            throw new ArgumentException($"导入内容过大（上限 {MaxJsonBytes / 1024} KB）");
        }

        JsonElement? data = null;
        try
        {
            data = JsonDocument.Parse(json).RootElement;
        }
        catch
        {
            throw new ArgumentException("不是合法的 JSON 文件");
        }
        if (data is not { ValueKind: JsonValueKind.Object } obj)
        {
            throw new ArgumentException("文件结构不是本应用的设置导出");
        }
        if (obj.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
            && fmt.GetString() == TransferFormat)
        {
            // 通过
        }
        else
        {
            throw new ArgumentException("文件格式不符：不是本应用的设置导出文件");
        }
        if (obj.TryGetProperty("version", out var ver) && ver.ValueKind is JsonValueKind.Number)
        {
            var v = ver.GetInt32();
            if (v > TransferVersion)
            {
                throw new ArgumentException($"文件版本（{v}）高于本程序支持的版本（{TransferVersion}），请先升级程序");
            }
        }
        else
        {
            throw new ArgumentException("文件缺少版本号，无法判断兼容性");
        }

        if (obj.TryGetProperty("settings", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            // 继续
        }
        else
        {
            throw new ArgumentException("文件里没有 settings 段");
        }

        ThemeMode? themeP = null; bool? advancedP = null;
        IReadOnlyList<string>? pinnedP = null; AlertRules? alertP = null; bool? autoStartP = null;
        var applied = new List<string>();
        var s = raw;

        if (s.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String)
        {
            var t = theme.GetString();
            if (t is "light" or "dark")
            {
                themeP = t == "dark" ? ThemeMode.Dark : ThemeMode.Light;
                applied.Add("theme");
            }
        }
        if (s.TryGetProperty("advancedMode", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            advancedP = am.GetBoolean();
            applied.Add("advancedMode");
        }
        if (s.TryGetProperty("pinned", out var pin) && pin.ValueKind == JsonValueKind.Array)
        {
            var pinned = pin.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            if (pinned.Count > 0)
            {
                pinnedP = pinned;
                applied.Add("pinned");
            }
        }
        if (s.TryGetProperty("alertRules", out var ar) && ar.ValueKind == JsonValueKind.Object)
        {
            // 契约缺口：C# store 的 alertRules 是整体替换（TS 为逐键浅合并）——
            // 导入侧先与当前态合并补全缺失键，再交 SaveState（对齐 TS「逐键收敛交给 stateStore」结果）。
            alertP = MergeAlertRulesWithCurrent(_store.Load().alertRules, ar);
            applied.Add("alertRules");
        }
        if (s.TryGetProperty("autoStart", out var asg) && asg.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            autoStartP = asg.GetBoolean();
            applied.Add("autoStart");
        }

        if (applied.Count == 0)
        {
            throw new ArgumentException("文件里没有任何可识别的设置项");
        }

        _store.SaveState(new AppStatePatch
        {
            theme = themeP,
            advancedMode = advancedP,
            pinned = pinnedP,
            alertRules = alertP,
            autoStart = autoStartP,
        });
        return applied;
    }

    /// <summary>导入 alertRules 与当前态合并（缺失键取当前值；0/负数收敛为 null/默认）。</summary>
    static AlertRules MergeAlertRulesWithCurrent(AlertRules current, JsonElement imported)
    {
        int? idle = imported.TryGetProperty("idleAlertMinutes", out var i)
            && i.ValueKind == JsonValueKind.Number && i.GetInt32() > 0 ? i.GetInt32() : null;
        bool smb1 = imported.TryGetProperty("smb1Alert", out var sb) && sb.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? sb.GetBoolean() : current.smb1Alert;
        bool weak = imported.TryGetProperty("weakPasswordAlert", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? w.GetBoolean() : current.weakPasswordAlert;
        int disk = imported.TryGetProperty("diskLowGb", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() >= 0
            ? d.GetInt32() : current.diskLowGb;
        return new AlertRules
        {
            idleAlertMinutes = idle,
            smb1Alert = smb1,
            weakPasswordAlert = weak,
            diskLowGb = disk,
        };
    }
}
