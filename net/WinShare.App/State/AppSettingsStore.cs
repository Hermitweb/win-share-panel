using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinShare.Core.Contracts;

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
            _cache = Normalize(new AppState
            {
                theme = s.theme,
                advancedMode = s.advancedMode,
                pinned = s.pinned,
                alertRules = s.alertRules,
                autoStart = s.autoStart,
                dashboardHistory = list,
                journal = s.journal,
            });
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

    // ===== 扩展字段 refreshInterval（TS 版没有；1|5|10|30 秒，其余收敛默认 10） =====

    int _refreshInterval = 10;

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
            _refreshInterval = seconds is 1 or 5 or 10 or 30 ? seconds : 10;
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
                return _cache!;
            }
        }
        catch
        {
            // 损坏则重建默认
        }
        _cache = NormalizeFromJson(null);
        return _cache!;
    }

    static int ParseRefreshInterval(string? json)
    {
        if (string.IsNullOrEmpty(json)) return 10;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("refreshInterval", out var v)
                && v.ValueKind == JsonValueKind.Number)
            {
                var n = v.GetInt32();
                return n is 1 or 5 or 10 or 30 ? n : 10;
            }
        }
        catch { /* 损坏按默认 */ }
        return 10;
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
        return new AppState
        {
            theme = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light,
            advancedMode = GetBool("advancedMode", true),
            pinned = pinned,
            alertRules = alert,
            autoStart = GetBool("autoStart", false),
            dashboardHistory = history,
            journal = journal,
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
            advancedMode = true,
            pinned = Array.Empty<string>(),
            alertRules = new AlertRules { idleAlertMinutes = null, smb1Alert = true, weakPasswordAlert = true, diskLowGb = 20 },
            autoStart = false,
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
            dashboardHistory = raw.dashboardHistory?.Where(p => p is { ts: > 0 }).TakeLast(HistoryMax).ToList()
                ?? d.dashboardHistory,
            journal = raw.journal?.Where(j => !string.IsNullOrEmpty(j.id)).TakeLast(JournalMax).ToList()
                ?? d.journal,
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
            dashboardHistory = patch.dashboardHistory ?? prev.dashboardHistory,
            journal = patch.journal ?? prev.journal,
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
                dashboardHistory = state.dashboardHistory,
                journal = state.journal,
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
