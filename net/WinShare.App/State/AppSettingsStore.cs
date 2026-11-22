using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.App.State;

/// <summary>
/// 应用级持久状态（C# 版 stateStore，对齐 electron/lib/stateStore.ts）：
/// 单 JSON 文件原子写（tmp + rename）、进程内缓存、容量上限防无限增长、
/// 旧文件缺字段/脏值逐字段收敛回默认。JSON 字段名与 TS 版 appstate.json 完全兼容（camelCase）。
/// </summary>
public sealed class AppSettingsStore
{
    public const int HistoryMax = 288;   // 5min 采样 × 24h
    public const int JournalMax = 200;
    public const int PinnedMax = 50;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JournalActionJsonConverter(),
            new ProtocolJsonConverter(),
            new PlainThemeConverter(),
            new NullableThemeConverter(),
        },
    };

    readonly string _dir;
    readonly object _gate = new();
    AppState? _cache;

    /// <param name="dir">state 目录（测试可注入 temp；默认 %APPDATA%\WinSharePanel\state）。</param>
    public AppSettingsStore(string? dir = null)
    {
        _dir = dir ?? DefaultDir();
    }

    public static string DefaultDir()
    {
        var baseDir = Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, "WinSharePanel", "state");
    }

    string StateFile => Path.Combine(_dir, "appstate.json");

    /// <summary>TS: loadState()（同步，带进程内缓存）。</summary>
    public AppState Load()
    {
        lock (_gate)
        {
            return LoadNoLock();
        }
    }

    /// <summary>TS: saveState(patch)（同步），与既有值浅合并，返回合并后全量。</summary>
    public AppState SaveState(AppStatePatch patch)
    {
        lock (_gate)
        {
            var prev = Load();
            var next = Merge(prev, patch);
            _cache = Normalize(next);
            Persist(_cache);
            return _cache;
        }
    }

    /// <summary>TS: pushHistory(point)：追加 + 288 上限。</summary>
    public void PushHistory(HistoryPoint point)
    {
        lock (_gate)
        {
            var s = Load();
            var list = s.dashboardHistory?.ToList() ?? new List<HistoryPoint>();
            list.Add(point);
            if (list.Count > HistoryMax) list.RemoveRange(0, list.Count - HistoryMax);
            // `with` 保留其余字段（含 v2.1 advanced）——新建 AppState 会把未列字段静默重置为默认
            _cache = Normalize(s with { dashboardHistory = list });
            Persist(_cache);
        }
    }

    /// <summary>TS: addJournal(entry)：id/ts 由 store 生成。</summary>
    public JournalEntry AddJournal(JournalEntryDraft entry)
    {
        lock (_gate)
        {
            int seq = Interlocked.Increment(ref _journalSeq);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var full = new JournalEntry
            {
                action = entry.action,
                protocol = entry.protocol,
                name = entry.name,
                detail = entry.detail,
                snapshot = entry.snapshot,
                undoable = entry.undoable,
                id = $"j{ts}-{seq}",
                ts = ts,
            };
            var s = Load();
            var list = s.journal?.ToList() ?? new List<JournalEntry>();
            list.Add(full);
            if (list.Count > JournalMax) list.RemoveRange(0, list.Count - JournalMax);
            _cache = Normalize(s with { journal = list });
            Persist(_cache);
            return full;
        }
    }

    /// <summary>TS: removeJournal(id)。</summary>
    public void RemoveJournal(string id)
    {
        lock (_gate)
        {
            var s = Load();
            var list = (s.journal?.ToList() ?? new List<JournalEntry>()).Where(j => j.id != id).ToList();
            _cache = Normalize(s with { journal = list });
            Persist(_cache);
        }
    }

    int _journalSeq;

    /// <summary>测试用进程内缓存复位（对齐 TS __resetStateCache）。</summary>
    public void ResetCacheForTesting()
    {
        lock (_gate) { _cache = null; _journalSeq = 0; }
    }

    // ===== 扩展字段 refreshInterval（TS 版没有；1|5|10|30 秒，其余收敛默认 30） =====

    int _refreshInterval = 60;

    public int GetRefreshInterval()
    {
        lock (_gate)
        {
            if (_cache is null) LoadNoLock();
            return _refreshInterval;
        }
    }

    public void SaveRefreshInterval(int seconds)
    {
        lock (_gate)
        {
            if (_cache is null) LoadNoLock(); // 先读文件态（其 LoadNoLock 会从文件补读 refreshInterval）
            _refreshInterval = seconds is 1 or 5 or 10 or 30 or 60 ? seconds : 60;
            Persist(_cache!);
        }
    }

    /// <summary>无锁版 Load（调用方已持 _gate）。</summary>
    AppState LoadNoLock()
    {
        if (_cache is { } c) return c;
        var raw = ReadRawJson();
        _refreshInterval = ParseRefreshInterval(raw);
        try
        {
            if (raw is not null)
            {
                _cache = NormalizeFromJson(raw);
                RepairJournalIfNeeded(_cache!);
                return _cache!;
            }
        }
        catch
        {
            // 损坏则重建默认
        }
        _cache = NormalizeFromJson(null);
        RepairJournalIfNeeded(_cache!);
        return _cache!;
    }

    /// <summary>
    /// v2.3 一次性历史乱码修复：旧版 Electron 时期的 PowerShell GBK 误读把 journal 的
    /// name/detail 写成了乱码（当前代码库已无该路径，只修历史数据）。
    /// 加载时逆向修复并落盘一次；幂等——修过的条目不会再匹配（见 JournalTextRepair）。
    /// </summary>
    void RepairJournalIfNeeded(AppState state)
    {
        var journal = state.journal;
        if (journal is null || journal.Count == 0) return;
        bool changed = false;
        var list = journal.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            string name = JournalTextRepair.Repair(e.name) ?? e.name;
            string? detail = e.detail is null ? null : JournalTextRepair.Repair(e.detail);
            if (!ReferenceEquals(name, e.name) || !ReferenceEquals(detail, e.detail))
            {
                list[i] = e with { name = name, detail = detail };
                changed = true;
            }
        }
        if (!changed) return;
        _cache = state with { journal = list };
        Persist(_cache); // 修好即落盘（调用方持 _gate；lock 可重入）
    }

    static int ParseRefreshInterval(string? json)
    {
        if (string.IsNullOrEmpty(json)) return 60;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("refreshInterval", out var v)
                && v.ValueKind == JsonValueKind.Number)
            {
                var n = v.GetInt32();
                return n is 1 or 5 or 10 or 30 or 60 ? n : 60;
            }
        }
        catch { /* 损坏按默认 */ }
        return 60;
    }

    string? ReadRawJson()
    {
        try { return System.IO.File.Exists(StateFile) ? System.IO.File.ReadAllText(StateFile) : null; }
        catch { return null; }
    }

    /// <summary>目录读写（测试钩子）。</summary>
    public string StateDir => _dir;

    // ===== 逐字段收敛（不用 spread，避免脏值穿透），与 TS normalize 一一对应 =====

    public static AppState NormalizeFromJson(string? json)
    {
        JsonElement? raw = null;
        if (json is not null)
        {
            try { raw = JsonDocument.Parse(json).RootElement; }
            catch { raw = null; } // 损坏 JSON → 重建默认（与 TS loadState catch 一致）
        }
        return Normalize(Parse(raw));
    }

    static AppState? Parse(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.Object } obj) return null;
        bool GetBool(string k, bool d) => obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True ? true
            : obj.TryGetProperty(k, out var v2) && v2.ValueKind == JsonValueKind.False ? false : d;

        var pinned = new List<string>();
        if (obj.TryGetProperty("pinned", out var pin) && pin.ValueKind == JsonValueKind.Array)
            foreach (var p in pin.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String) pinned.Add(p.GetString()!);

        var history = new List<HistoryPoint>();
        if (obj.TryGetProperty("dashboardHistory", out var dh) && dh.ValueKind == JsonValueKind.Array)
            foreach (var p in dh.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number)
                {
                    int I(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    history.Add(new HistoryPoint { ts = ts.GetInt64(), sessions = I("sessions"), openFiles = I("openFiles") });
                }

        var journal = new List<JournalEntry>();
        if (obj.TryGetProperty("journal", out var jn) && jn.ValueKind == JsonValueKind.Array)
            foreach (var j in jn.EnumerateArray())
                if (j.ValueKind == JsonValueKind.Object &&
                    j.TryGetProperty("id", out var jid) && jid.ValueKind == JsonValueKind.String)
                {
                    journal.Add(new JournalEntry
                    {
                        id = jid.GetString()!,
                        ts = j.TryGetProperty("ts", out var jts) && jts.ValueKind == JsonValueKind.Number ? jts.GetInt64() : 0,
                        action = ParseEnum(j, "action", JournalAction.Create),
                        protocol = ParseEnum(j, "protocol", Protocol.Smb),
                        name = j.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : "",
                        detail = j.TryGetProperty("detail", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null,
                        snapshot = null, // C# 契约用 object? 承载；TS 联合快照解析由服务层负责
                        undoable = j.TryGetProperty("undoable", out var ud) && ud.ValueKind == JsonValueKind.True,
                    });
                }

        var alert = new AlertRules
        {
            idleAlertMinutes = obj.TryGetProperty("alertRules", out var ar) && ar.ValueKind == JsonValueKind.Object
                && ar.TryGetProperty("idleAlertMinutes", out var idle) && idle.ValueKind == JsonValueKind.Number
                && idle.GetInt32() > 0 ? idle.GetInt32() : null,
            smb1Alert = GetAlertBool(obj, "smb1Alert", true),
            weakPasswordAlert = GetAlertBool(obj, "weakPasswordAlert", true),
            diskLowGb = obj.TryGetProperty("alertRules", out var ar2) && ar2.ValueKind == JsonValueKind.Object
                && ar2.TryGetProperty("diskLowGb", out var dl) && dl.ValueKind == JsonValueKind.Number && dl.GetInt32() >= 0
                ? dl.GetInt32() : 20,
        };

        string? theme = obj.TryGetProperty("theme", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : null;

        // v2.1 高级设置（缺字段/脏值逐键回默认；收敛规则复用 Core StateStore.NormalizeAdvanced 防漂移）
        AdvancedSettings advanced = new();
        if (obj.TryGetProperty("advanced", out var adv) && adv.ValueKind == JsonValueKind.Object)
        {
            advanced = WinShare.Core.Lib.StateStore.NormalizeAdvanced(new AdvancedSettings
            {
                updateRepo = adv.TryGetProperty("updateRepo", out var ur) && ur.ValueKind == JsonValueKind.String
                    ? ur.GetString() ?? ""
                    : advanced.updateRepo,
                passwordAgeWarnDays = adv.TryGetProperty("passwordAgeWarnDays", out var pwd) && pwd.ValueKind == JsonValueKind.Number
                    ? pwd.GetInt32() : advanced.passwordAgeWarnDays,
                logMaxSizeMb = adv.TryGetProperty("logMaxSizeMb", out var lm) && lm.ValueKind == JsonValueKind.Number
                    ? lm.GetInt32() : advanced.logMaxSizeMb,
                logMaxFiles = adv.TryGetProperty("logMaxFiles", out var lf) && lf.ValueKind == JsonValueKind.Number
                    ? lf.GetInt32() : advanced.logMaxFiles,
                ftpPassiveFrom = adv.TryGetProperty("ftpPassiveFrom", out var ff) && ff.ValueKind == JsonValueKind.Number
                    ? ff.GetInt32() : advanced.ftpPassiveFrom,
                ftpPassiveTo = adv.TryGetProperty("ftpPassiveTo", out var ft) && ft.ValueKind == JsonValueKind.Number
                    ? ft.GetInt32() : advanced.ftpPassiveTo,
            });
        }

        return new AppState
        {
            theme = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light,
            advancedMode = GetBool("advancedMode", false), // v2.2.0：新装默认新手模式（向导可达）
            pinned = pinned,
            alertRules = alert,
            autoStart = GetBool("autoStart", false),
            showInTaskbar = GetBool("showInTaskbar", true),
            closeToTray = GetBool("closeToTray", true),
            dashboardHistory = history,
            journal = journal,
            advanced = advanced,
        };
    }

    static bool GetAlertBool(JsonElement obj, string key, bool def)
    {
        if (!obj.TryGetProperty("alertRules", out var ar) || ar.ValueKind != JsonValueKind.Object) return def;
        if (!ar.TryGetProperty(key, out var v)) return def;
        return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : def;
    }

    static T ParseEnum<T>(JsonElement e, string key, T def) where T : struct
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return def;
        var s = v.GetString() ?? "";
        foreach (var name in Enum.GetNames(typeof(T)))
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<T>(name, true);
        return def;
    }

    /// <summary>脏值收敛：逐字段显式收敛回默认（与 TS normalize 一致）。</summary>
    public static AppState Normalize(AppState raw)
    {
        var d = new AppState
        {
            theme = ThemeMode.Light,
            advancedMode = false, // v2.2.0：新装默认新手模式（与 Core Defaults 对齐）
            pinned = Array.Empty<string>(),
            alertRules = new AlertRules { idleAlertMinutes = null, smb1Alert = true, weakPasswordAlert = true, diskLowGb = 20 },
            autoStart = false,
            showInTaskbar = true,
            closeToTray = true,
            dashboardHistory = Array.Empty<HistoryPoint>(),
            journal = Array.Empty<JournalEntry>(),
        };
        if (raw is null) return d;
        return new AppState
        {
            theme = raw.theme,
            advancedMode = raw.advancedMode,
            pinned = raw.pinned?.Where(x => !string.IsNullOrEmpty(x)).TakeLast(PinnedMax).ToList() ?? d.pinned,
            alertRules = raw.alertRules is { } r ? new AlertRules
            {
                idleAlertMinutes = (r.idleAlertMinutes ?? 0) > 0 ? r.idleAlertMinutes : null,
                smb1Alert = r.smb1Alert,
                weakPasswordAlert = r.weakPasswordAlert,
                diskLowGb = r.diskLowGb >= 0 ? r.diskLowGb : 20,
            } : d.alertRules,
            autoStart = raw.autoStart,
            showInTaskbar = raw.showInTaskbar,
            closeToTray = raw.closeToTray,
            dashboardHistory = raw.dashboardHistory?.Where(p => p is { ts: > 0 }).TakeLast(HistoryMax).ToList()
                ?? d.dashboardHistory,
            journal = raw.journal?.Where(j => !string.IsNullOrEmpty(j.id)).TakeLast(JournalMax).ToList()
                ?? d.journal,
            advanced = WinShare.Core.Lib.StateStore.NormalizeAdvanced(raw.advanced),
        };
    }

    static AppState Merge(AppState prev, AppStatePatch patch)
    {
        // alertRules 浅合并：调用方只改一项时其余项保持原值（与 TS 一致）
        var mergedAlert = prev.alertRules;
        if (patch.alertRules is { } p)
        {
            // 契约 AppStatePatch.alertRules 为整体 AlertRules（无逐键 Partial——与 TS 差异，已列缺口清单）：整体替换
            mergedAlert = p;
        }
        return new AppState
        {
            theme = patch.theme ?? prev.theme,
            advancedMode = patch.advancedMode ?? prev.advancedMode,
            pinned = patch.pinned ?? prev.pinned,
            alertRules = mergedAlert,
            autoStart = patch.autoStart ?? prev.autoStart,
            showInTaskbar = patch.showInTaskbar ?? prev.showInTaskbar,
            closeToTray = patch.closeToTray ?? prev.closeToTray,
            dashboardHistory = patch.dashboardHistory ?? prev.dashboardHistory,
            journal = patch.journal ?? prev.journal,
            advanced = patch.advanced ?? prev.advanced,
        };
    }

    void Persist(AppState state)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // 序列化扩展 DTO：AppState 全字段 + refreshInterval（C# 扩展键，与 TS 字段名同风格 camelCase）
            var file = new StateFileDto
            {
                theme = state.theme,
                advancedMode = state.advancedMode,
                pinned = state.pinned,
                alertRules = state.alertRules,
                autoStart = state.autoStart,
                showInTaskbar = state.showInTaskbar, // v2.2.0：窗口行为落盘（此前漏拷导致设置卡写入后读回恒为默认值）
                closeToTray = state.closeToTray,
                dashboardHistory = state.dashboardHistory,
                journal = state.journal,
                advanced = state.advanced, // v2.1：高级设置落盘（显式拷贝，防默认值静默覆盖）
                refreshInterval = _refreshInterval,
            };
            var json = JsonSerializer.Serialize(file, JsonOpts);
            var tmp = StateFile + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, StateFile, overwrite: true);
        }
        catch
        {
            // 持久化失败不影响内存态（下次写重试）——与 TS 一致
        }
    }

    /// <summary>落盘 DTO = 契约 AppState 全字段 + refreshInterval 扩展键（1|5|10|30）。</summary>
    sealed record StateFileDto : AppState
    {
        public int refreshInterval { get; init; }
    }

    // ===== JSON 值兼容转换（appstate.json 内枚举一律落 TS 小写字面量） =====

    sealed class PlainThemeConverter : JsonConverter<ThemeMode>
        {
            public override ThemeMode Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
            {
                var s = r.GetString() ?? "";
                return s == "dark" ? ThemeMode.Dark : ThemeMode.Light;
            }
            public override void Write(Utf8JsonWriter w, ThemeMode v, JsonSerializerOptions o) =>
                w.WriteStringValue(v == ThemeMode.Dark ? "dark" : "light");
        }
        sealed class NullableThemeConverter : JsonConverter<ThemeMode?>
        {
            public override ThemeMode? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
                r.TokenType == System.Text.Json.JsonTokenType.Null ? null : r.GetString() == "dark" ? ThemeMode.Dark : ThemeMode.Light;
            public override void Write(Utf8JsonWriter w, ThemeMode? v, JsonSerializerOptions o)
            {
                if (v is null) w.WriteNullValue();
                else w.WriteStringValue(v == ThemeMode.Dark ? "dark" : "light");
            }
        }

    sealed class ProtocolJsonConverter : JsonConverter<Protocol>
    {
        static readonly Dictionary<Protocol, string> Map = new()
        {
            [Protocol.Smb] = "smb", [Protocol.Nfs] = "nfs",
            [Protocol.Ftp] = "ftp", [Protocol.Webdav] = "webdav",
        };
        public override Protocol Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var s = r.GetString() ?? "";
            foreach (var kv in Map) if (kv.Value == s) return kv.Key;
            return Protocol.Smb;
        }
        public override void Write(Utf8JsonWriter w, Protocol v, JsonSerializerOptions o) => w.WriteStringValue(Map[v]);
    }

    sealed class JournalActionJsonConverter : JsonConverter<JournalAction>
    {
        static readonly Dictionary<JournalAction, string> Map = new()
        {
            [JournalAction.Create] = "create", [JournalAction.Delete] = "delete",
            [JournalAction.ToggleOff] = "toggle-off", [JournalAction.Permset] = "permset",
        };
        public override JournalAction Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var s = r.GetString() ?? "";
            foreach (var kv in Map) if (kv.Value == s) return kv.Key;
            return JournalAction.Create;
        }
        public override void Write(Utf8JsonWriter w, JournalAction v, JsonSerializerOptions o) => w.WriteStringValue(Map[v]);
    }
}
