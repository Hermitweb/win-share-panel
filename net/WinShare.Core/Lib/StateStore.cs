// 移植自 electron/lib/stateStore.ts（单 JSON 文件原子写；读取带进程内缓存；容量上限防止无限增长）。
// 文件布局与 TS 对齐：%APPDATA%/WinSharePanel/state/appstate.json（非 win32 → ~/.winshare-panel）。
// JSON 键与 TS 一致（camelCase、theme 'light'/'dark'、journal action 'create'/'delete'/'toggle-off'/'permset'）。
using System.Text.Json;
using System.Text.Json.Nodes;
using WinShare.Core.Contracts;

namespace WinShare.Core.Lib;

/// <summary>
/// 应用级持久状态实现（TS: electron/lib/stateStore.ts）。
/// 单例语义经 <see cref="Default"/>；测试可传 dataDirOverride 指向临时根目录
/// （文件仍落在 {root}/WinSharePanel/state/appstate.json，与 TS 用 APPDATA 钉死同一布局）。
/// </summary>
public sealed class StateStore : IStateStore
{
    private const int HistoryMax = 288; // 5min 采样 × 24h
    private const int JournalMax = 200;
    private const int PinnedMax = 50;

    private static readonly AppState Defaults = new()
    {
        theme = ThemeMode.Light,
        advancedMode = true,
        pinned = Array.Empty<string>(),
        alertRules = new AlertRules { idleAlertMinutes = null, smb1Alert = true, weakPasswordAlert = true, diskLowGb = 20 },
        autoStart = false,
        dashboardHistory = Array.Empty<HistoryPoint>(),
        journal = Array.Empty<JournalEntry>(),
    };

    private readonly object _gate = new();
    private readonly string? _dataDirOverride;
    private AppState? _cache;
    private int _journalSeq;

    /// <summary>生产侧默认实例（TS 模块级状态等价）。</summary>
    public static StateStore Default { get; } = new();

    public StateStore(string? dataDirOverride = null) => _dataDirOverride = dataDirOverride;

    /// <summary>TS: stateDir()/stateFile()（每次调用现算，目录可随 APPDATA 变化）。</summary>
    public string StateFile
    {
        get
        {
            string root = _dataDirOverride ?? DefaultBaseDir();
            return Path.Combine(root, "WinSharePanel", "state", "appstate.json");
        }
    }

    /// <summary>TS: base = win32 ? process.env.APPDATA || homedir() : join(homedir(), '.winshare-panel')。</summary>
    public static string DefaultBaseDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appdata = Environment.GetEnvironmentVariable("APPDATA");
            return string.IsNullOrEmpty(appdata) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : appdata!;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winshare-panel");
    }

    // === IStateStore ===

    /// <summary>TS: loadState()——缓存命中不再读文件；损坏/非对象回落默认不抛错。</summary>
    public AppState LoadState()
    {
        lock (_gate)
        {
            // TS: if (cache) return cache —— 缓存命中不再读文件
            _cache ??= TryRead() ?? Clone(Defaults);
            return _cache;
        }
    }

    /// <summary>
    /// TS: saveState(patch)——prev + patch 浅合并 → normalize 逐键收敛 → 原子落盘（tmp + rename）；
    /// 持久化失败不影响内存态（下次写重试）。
    /// </summary>
    public AppState SaveState(AppStatePatch patch)
    {
        lock (_gate)
        {
            var prev = _cache ?? (_cache = TryRead() ?? Clone(Defaults));
            _cache = ApplyPatch(prev, patch);
            TryPersist(_cache);
            return _cache;
        }
    }

    /// <summary>TS: pushHistory(point)。</summary>
    public void PushHistory(HistoryPoint point)
    {
        lock (_gate)
        {
            var s = _cache ?? (_cache = TryRead() ?? Clone(Defaults));
            _cache = ApplyPatch(s, new AppStatePatch { dashboardHistory = s.dashboardHistory.Append(point).ToList() });
            TryPersist(_cache);
        }
    }

    /// <summary>TS: addJournal(entry)——id = `j{Date.now()}-{++journalSeq}`。</summary>
    public JournalEntry AddJournal(JournalEntryDraft entry)
    {
        lock (_gate)
        {
            var s = _cache ?? (_cache = TryRead() ?? Clone(Defaults));
            _journalSeq++;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var full = new JournalEntry
            {
                id = $"j{now}-{_journalSeq}",
                ts = now,
                action = entry.action,
                protocol = entry.protocol,
                name = entry.name,
                detail = entry.detail,
                snapshot = entry.snapshot,
                undoable = entry.undoable,
            };
            _cache = ApplyPatch(s, new AppStatePatch { journal = s.journal.Append(full).ToList() });
            TryPersist(_cache);
            return full;
        }
    }

    /// <summary>TS: removeJournal(id)——删除不存在的 id 是安全的 no-op。</summary>
    public void RemoveJournal(string id)
    {
        lock (_gate)
        {
            var s = _cache ?? (_cache = TryRead() ?? Clone(Defaults));
            _cache = ApplyPatch(s, new AppStatePatch { journal = s.journal.Where(j => j.id != id).ToList() });
            TryPersist(_cache);
        }
    }

    /// <summary>TS: __resetStateCache()。</summary>
    public void ResetStateCacheForTesting()
    {
        lock (_gate)
        {
            _cache = null;
            _journalSeq = 0;
        }
    }

    // === 内部 ===

    private AppState ApplyPatch(AppState prev, AppStatePatch patch) =>
        NormalizeInMemory(Merge(prev, patch));

    private static AppState Merge(AppState prev, AppStatePatch patch) => new()
    {
        theme = patch.theme ?? prev.theme,
        advancedMode = patch.advancedMode ?? prev.advancedMode,
        pinned = patch.pinned ?? prev.pinned,
        alertRules = MergeAlertRules(prev.alertRules, patch.alertRules),
        autoStart = patch.autoStart ?? prev.autoStart,
        dashboardHistory = patch.dashboardHistory ?? prev.dashboardHistory,
        journal = patch.journal ?? prev.journal,
    };

    /// <summary>
    /// alertRules 浅合并（对应 TS {...prev.alertRules, ...(patch.alertRules ?? {})}）：
    /// 契约的 AlertRules 中 bool/int 字段非可空，无法表达"未改"，故 patch 非 null 时整体替换，
    /// 仅 int? 的 idleAlertMinutes 参与逐键浅合并（null = 未改，保持 prev 值）。
    /// </summary>
    private static AlertRules MergeAlertRules(AlertRules prev, AlertRules? patch)
    {
        if (patch is null) return prev;
        return new AlertRules
        {
            idleAlertMinutes = patch.idleAlertMinutes ?? prev.idleAlertMinutes,
            smb1Alert = patch.smb1Alert,
            weakPasswordAlert = patch.weakPasswordAlert,
            diskLowGb = patch.diskLowGb,
        };
    }

    /// <summary>
    /// 内存态收敛（TS normalize 的保存路径等价）：容量截断保留最新、
    /// idleAlertMinutes ≤ 0 回默认 null、diskLowGb &lt; 0 回默认 20。
    /// </summary>
    private static AppState NormalizeInMemory(AppState s)
    {
        var d = Defaults;
        return new AppState
        {
            theme = s.theme,
            advancedMode = s.advancedMode,
            pinned = (s.pinned ?? Array.Empty<string>()).TakeLast(PinnedMax).ToList(),
            alertRules = new AlertRules
            {
                idleAlertMinutes = s.alertRules.idleAlertMinutes is { } m && m > 0 ? JsMathRound(m) : d.alertRules.idleAlertMinutes,
                smb1Alert = s.alertRules.smb1Alert,
                weakPasswordAlert = s.alertRules.weakPasswordAlert,
                diskLowGb = s.alertRules.diskLowGb >= 0 ? s.alertRules.diskLowGb : d.alertRules.diskLowGb,
            },
            autoStart = s.autoStart,
            dashboardHistory = (s.dashboardHistory ?? Array.Empty<HistoryPoint>()).TakeLast(HistoryMax).ToList(),
            journal = (s.journal ?? Array.Empty<JournalEntry>()).TakeLast(JournalMax).ToList(),
        };
    }

    private AppState? TryRead()
    {
        try
        {
            string file = StateFile;
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return NormalizeJson(doc.RootElement.Clone());
        }
        catch
        {
            // 损坏则重建默认
            return null;
        }
    }

    /// <summary>
    /// 旧文件缺字段/多字段都要能安全升级：逐字段显式收敛回默认值（不用 spread，避免脏值穿透）。
    /// 未知字段无承载位（契约 DTO 收敛回已知 schema）——与 TS 语义一致。
    /// </summary>
    private static AppState NormalizeJson(JsonElement root)
    {
        var d = Defaults;
        if (root.ValueKind is not JsonValueKind.Object) return Clone(d);

        // 脏值逐键拒收：类型不符一律回默认
        ThemeMode theme = root.TryGetProperty("theme", out var t)
            && t.ValueKind == JsonValueKind.String && t.GetString() == "dark"
            ? ThemeMode.Dark
            : ThemeMode.Light;

        bool advancedMode = root.TryGetProperty("advancedMode", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? am.GetBoolean()
            : d.advancedMode;

        var pinned = new List<string>();
        if (root.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in p.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String) pinned.Add(el.GetString()!);
            }
        }

        JsonElement ar = root.TryGetProperty("alertRules", out var arEl) && arEl.ValueKind == JsonValueKind.Object
            ? arEl
            : default;
        double? idle = ArNumber(ar, "idleAlertMinutes");
        double? disk = ArNumber(ar, "diskLowGb");
        var rules = new AlertRules
        {
            idleAlertMinutes = idle is { } iv && iv > 0 ? JsMathRound(iv) : d.alertRules.idleAlertMinutes,
            smb1Alert = BoolProp(ar, "smb1Alert", d.alertRules.smb1Alert),
            weakPasswordAlert = BoolProp(ar, "weakPasswordAlert", d.alertRules.weakPasswordAlert),
            diskLowGb = disk is { } dv && dv >= 0 ? JsMathRound(dv) : d.alertRules.diskLowGb,
        };

        bool autoStart = root.TryGetProperty("autoStart", out var asEl) && asEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? asEl.GetBoolean()
            : d.autoStart;

        var hist = new List<HistoryPoint>();
        if (root.TryGetProperty("dashboardHistory", out var h) && h.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in h.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object) continue;
                if (!item.TryGetProperty("ts", out var ts) || ts.ValueKind is not JsonValueKind.Number) continue;
                hist.Add(new HistoryPoint
                {
                    ts = (long)ts.GetDouble(),
                    sessions = IntProp(item, "sessions"),
                    openFiles = IntProp(item, "openFiles"),
                });
            }
        }

        var journal = new List<JournalEntry>();
        if (root.TryGetProperty("journal", out var j) && j.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in j.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind is not JsonValueKind.String) continue;
                journal.Add(new JournalEntry
                {
                    id = id.GetString()!,
                    ts = item.TryGetProperty("ts", out var jts) && jts.ValueKind == JsonValueKind.Number
                        ? (long)jts.GetDouble()
                        : 0,
                    action = ParseJournalAction(item),
                    protocol = ParseProtocol(item),
                    name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString()!
                        : "",
                    detail = item.TryGetProperty("detail", out var dt) && dt.ValueKind == JsonValueKind.String
                        ? dt.GetString()
                        : null,
                    snapshot = item.TryGetProperty("snapshot", out var sn)
                        && sn.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                        ? JsonNode.Parse(sn.GetRawText())
                        : null,
                    undoable = item.TryGetProperty("undoable", out var ud)
                        && ud.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && ud.GetBoolean(),
                });
            }
        }

        return new AppState
        {
            theme = theme,
            advancedMode = advancedMode,
            pinned = pinned.TakeLast(PinnedMax).ToList(),
            alertRules = rules,
            autoStart = autoStart,
            dashboardHistory = hist.TakeLast(HistoryMax).ToList(),
            journal = journal.TakeLast(JournalMax).ToList(),
        };
    }

    private static double? ArNumber(JsonElement ar, string key) =>
        ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static bool BoolProp(JsonElement parent, string key, bool fallback) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static int IntProp(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)v.GetDouble()
            : 0;

    /// <summary>TS action 字符串域：'create' | 'delete' | 'toggle-off' | 'permset'。</summary>
    private static JournalAction ParseJournalAction(JsonElement e)
    {
        if (e.ValueKind is not JsonValueKind.Object || !e.TryGetProperty("action", out var a)
            || a.ValueKind is not JsonValueKind.String)
        {
            return JournalAction.Create;
        }
        return a.GetString()! switch
        {
            "delete" => JournalAction.Delete,
            "toggle-off" => JournalAction.ToggleOff,
            "permset" => JournalAction.Permset,
            _ => JournalAction.Create, // 'create' 与未知值一律收敛默认
        };
    }

    /// <summary>TS protocol 字符串域：'smb' | 'nfs' | 'ftp' | 'webdav'。</summary>
    private static Protocol ParseProtocol(JsonElement e)
    {
        if (e.ValueKind is not JsonValueKind.Object || !e.TryGetProperty("protocol", out var p)
            || p.ValueKind is not JsonValueKind.String)
        {
            return Protocol.Smb;
        }
        return p.GetString()! switch
        {
            "nfs" => Protocol.Nfs,
            "ftp" => Protocol.Ftp,
            "webdav" => Protocol.Webdav,
            _ => Protocol.Smb,
        };
    }

    private void TryPersist(AppState state)
    {
        try
        {
            string file = StateFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(StateToJson(state), JsonCompat.Indented));
            File.Move(tmp, file, overwrite: true); // 原子替换（同卷 rename 语义）
        }
        catch
        {
            // 持久化失败不影响内存态（下次写重试）
        }
    }

    /// <summary>AppState → JSON（键/枚举字符串与 TS 文件布局逐字对齐）。</summary>
    private static JsonNode? StateToJson(AppState s)
    {
        var o = new JsonObject
        {
            ["theme"] = s.theme == ThemeMode.Dark ? "dark" : "light",
            ["advancedMode"] = s.advancedMode,
            ["pinned"] = new JsonArray(s.pinned.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["alertRules"] = new JsonObject
            {
                ["idleAlertMinutes"] = s.alertRules.idleAlertMinutes is int m ? JsonValue.Create(m) : JsonValue.Create<string?>(null),
                ["smb1Alert"] = s.alertRules.smb1Alert,
                ["weakPasswordAlert"] = s.alertRules.weakPasswordAlert,
                ["diskLowGb"] = s.alertRules.diskLowGb,
            },
            ["autoStart"] = s.autoStart,
            ["dashboardHistory"] = new JsonArray(
                s.dashboardHistory.Select(h => (JsonNode)new JsonObject
                {
                    ["ts"] = h.ts,
                    ["sessions"] = h.sessions,
                    ["openFiles"] = h.openFiles
                }).ToArray()),
            ["journal"] = new JsonArray(s.journal.Select(JournalToJson).ToArray()),
        };
        return o;
    }

    private static JsonNode JournalToJson(JournalEntry j)
    {
        var o = new JsonObject
        {
            ["id"] = j.id,
            ["ts"] = j.ts,
            ["action"] = j.action switch
            {
                JournalAction.Delete => "delete",
                JournalAction.ToggleOff => "toggle-off",
                JournalAction.Permset => "permset",
                _ => "create",
            },
            ["protocol"] = j.protocol switch
            {
                Protocol.Nfs => "nfs",
                Protocol.Ftp => "ftp",
                Protocol.Webdav => "webdav",
                _ => "smb",
            },
            ["name"] = j.name,
            ["undoable"] = j.undoable,
        };
        if (j.detail is not null) o["detail"] = j.detail;
        if (j.snapshot is not null)
        {
            o["snapshot"] = j.snapshot switch
            {
                JsonNode n => n,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                var other => JsonSerializer.SerializeToNode(other, other.GetType()),
            };
        }
        return o;
    }

    private static AppState Clone(AppState s) => new()
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
        dashboardHistory = s.dashboardHistory.ToList(),
        journal = s.journal.ToList(),
    };

    /// <summary>JS Math.round 等价（half-up）；.NET Math.Round 为 banker's rounding，不能直接用。</summary>
    private static int JsMathRound(double v) => (int)Math.Floor(v + 0.5);
}
