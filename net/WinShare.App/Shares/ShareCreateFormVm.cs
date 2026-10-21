using System.ComponentModel;
using CoreProtocol = WinShare.Core.Contracts.Protocol; // T3d 最小修复：VM 属性 Protocol(string) 遮蔽枚举
using System.Runtime.CompilerServices;
using System.Text;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 新建共享的常见场景预设（逐条对齐 Shares.tsx CREATE_SCENARIO_PRESETS；
/// 填入后仍可逐项调整。UsesAdvanced = presetUsesAdvanced：
/// 新手模式（advancedMode=false）下会写高级参数的预设一并收敛——
/// 点了看不见改了啥，比不给更糟。
/// </summary>
public sealed record ScenarioPreset(string Name, string? Tip, IReadOnlyDictionary<string, object?> Values, bool UsesAdvanced)
{
    public override string ToString() => Name;
}

public static class CreateScenarioPresets
{
    /// <summary>高级参数键集（Shares.tsx ADVANCED_FORM_KEYS）：缓存/枚举/并发上限/加密/root 访问等运维深水区。</summary>
    public static readonly HashSet<string> AdvancedFormKeys = new()
    {
        "encrypted", "encryptData", "shareShadowCopy", "folderEnumerationMode",
        "cachingMode", "concurrentUserLimit", "enableUnmappedAccess",
        "allowRootAccess", "anonymousUid", "anonymousGid",
    };

    static bool UsesAdvanced(IReadOnlyDictionary<string, object?> values) =>
        values.Keys.Any(k => AdvancedFormKeys.Contains(k));

    /// <summary>TS CREATE_SCENARIO_PRESETS[proto].filter(p => advancedMode || !presetUsesAdvanced(p.values))。</summary>
    public static IReadOnlyList<ScenarioPreset> ForProtocol(string protocol, bool advancedMode)
    {
        var all = protocol switch
        {
            "smb" => new ScenarioPreset[]
            {
                new ScenarioPreset("团队协作", "Administrators 完全控制，Users 可更改",
                    new Dictionary<string, object?> { ["fullAccess"] = "Administrators", ["changeAccess"] = "Users" },
                    false),
                new ScenarioPreset("公开只读", "Everyone 只读发布",
                    new Dictionary<string, object?> { ["readAccess"] = "Everyone", ["changeAccess"] = "", ["fullAccess"] = "" },
                    false),
                new ScenarioPreset("私有加密", "仅管理员 + 传输加密 + 按访问枚举",
                    new Dictionary<string, object?>
                    {
                        ["fullAccess"] = "Administrators", ["changeAccess"] = "", ["readAccess"] = "",
                        ["encrypted"] = true, ["encryptData"] = true, ["folderEnumerationMode"] = "AccessBased",
                    },
                    false), // 标记位稍后按键集重算
            },
            "nfs" => new ScenarioPreset[]
            {
                new ScenarioPreset("只读发布", null,
                    new Dictionary<string, object?> { ["nfsPermission"] = "ro", ["authentication"] = "sys" }, false),
                new ScenarioPreset("读写共享", null,
                    new Dictionary<string, object?> { ["nfsPermission"] = "rw", ["authentication"] = "sys" }, false),
                new ScenarioPreset("Kerberos 安全共享", "krb5i 完整性校验，禁 root 与未映射访问",
                    new Dictionary<string, object?>
                    {
                        ["nfsPermission"] = "rw", ["authentication"] = "krb5i",
                        ["allowRootAccess"] = false, ["enableUnmappedAccess"] = false,
                    }, false),
            },
            "ftp" => new ScenarioPreset[]
            {
                new ScenarioPreset("安全分发", "强制 TLS + 基本认证",
                    new Dictionary<string, object?> { ["sslPolicy"] = "SslRequire", ["authMode"] = "basic", ["port"] = 21 }, false),
                new ScenarioPreset("匿名下载", "TLS 可选 + 匿名登录",
                    new Dictionary<string, object?> { ["sslPolicy"] = "SslAllow", ["authMode"] = "anonymous" }, false),
                new ScenarioPreset("内网共享", "TLS 可选 + 基本认证",
                    new Dictionary<string, object?> { ["sslPolicy"] = "SslAllow", ["authMode"] = "basic" }, false),
            },
            "webdav" => new ScenarioPreset[]
            {
                new ScenarioPreset("只读发布", null,
                    new Dictionary<string, object?> { ["anonymousEnabled"] = false }, false),
                new ScenarioPreset("匿名投递", "站点级匿名认证；写入还需在服务器配置开启 authoring",
                    new Dictionary<string, object?> { ["anonymousEnabled"] = true }, false),
            },
            _ => Array.Empty<ScenarioPreset>(),
        };
        return all
            .Select(p => p with { UsesAdvanced = UsesAdvanced(p.Values) })
            .Where(p => advancedMode || !p.UsesAdvanced)
            .ToList();
    }
}

/// <summary>
/// 新建共享表单 ViewModel（对齐 Shares.tsx 新建 Modal + fillPathAndName 语义）：
/// - 路径落地顺带带出共享名（shareNameFromPath）；用户一旦手改过名字（NameTouched），
///   路径变化不再覆盖自动值——自动填值永远不得覆盖用户输入。
/// - BuildCreateInput：新手模式隐藏项一律不提交（ADVANCED_FORM_KEYS 收敛，
///   表单残留上次专家模式取值静默入库 = 替用户做主）；
///   readAccess 默认授权 DEFAULT_READ_ACCESS（访问控制未展开时的补齐值）。
/// </summary>
public sealed class ShareCreateFormVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ===== 通用字段（initialValues: protocol='smb' / nfsPermission='rw' / readAccess=Users） =====
    public string Protocol { get; set; } = "smb";
    public string? PresetId { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>用户是否已手改过名字（TS nameTouchedRef）。</summary>
    public bool NameTouched { get; set; }

    // ===== SMB 专有 =====
    public bool Encrypted { get; set; }
    public string FullAccess { get; set; } = "";
    public string ChangeAccess { get; set; } = "";
    public string ReadAccess { get; set; } = string.Join(",", ShareNameUtils.DefaultReadAccess);
    public string NoAccess { get; set; } = "";
    public bool EncryptData { get; set; }
    public bool ShareShadowCopy { get; set; }
    public string FolderEnumerationMode { get; set; } = "Unrestricted";
    public string CachingMode { get; set; } = "Manual";
    public int ConcurrentUserLimit { get; set; }

    // ===== NFS 专有 =====
    public string Authentication { get; set; } = "";          // "krb5,krb5i,krb5p,sys"
    public string NfsPermission { get; set; } = "rw";
    public bool EnableUnmappedAccess { get; set; }
    public bool AllowRootAccess { get; set; }
    public int AnonymousUid { get; set; }
    public int AnonymousGid { get; set; }

    // ===== FTP / WebDAV 专有 =====
    public int Port { get; set; } = 21;
    public string SslPolicy { get; set; } = "SslAllow";
    public string AuthMode { get; set; } = "basic";
    public bool AnonymousEnabled { get; set; }

    // ===== 校验（对齐 antd rules：name/path 必填） =====
    public bool Validate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "请输入共享名";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Path))
        {
            error = "请输入或拖入路径";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// TS fillPathAndName：路径落地后顺带把共享名带出来；
    /// 用户已手改过名字则只改路径（自动值不得覆盖用户输入）。
    /// </summary>
    public void FillPathAndName(string path)
    {
        Path = path;
        if (NameTouched) return;
        var auto = ShareNameUtils.ShareNameFromPath(path);
        if (auto.Length > 0) Name = auto;
    }

    /// <summary>TS handlePathInput：路径手输变化同样带名（浏览/拖拽走 FillPathAndName）。</summary>
    public void OnPathText(string text)
    {
        Path = text;
        if (NameTouched) return;
        var auto = ShareNameUtils.ShareNameFromPath(text);
        if (auto.Length > 0) Name = auto;
    }

    /// <summary>套用场景预设（TS form.setFieldsValue(p.values)：逐项填入，仍可调整）。</summary>
    public void ApplyScenario(ScenarioPreset preset)
    {
        foreach (var (key, value) in preset.Values)
        {
            ApplyValue(key, value?.ToString() ?? "");
        }
    }

    void ApplyValue(string key, string value)
    {
        switch (key)
        {
            case "fullAccess": FullAccess = value; break;
            case "changeAccess": ChangeAccess = value; break;
            case "readAccess": ReadAccess = value; break;
            case "noAccess": NoAccess = value; break;
            case "encrypted": Encrypted = value == "True"; break;
            case "encryptData": EncryptData = value == "True"; break;
            case "folderEnumerationMode": FolderEnumerationMode = value.Length > 0 ? value : FolderEnumerationMode; break;
            case "nfsPermission": NfsPermission = value; break;
            case "authentication": Authentication = value; break;
            case "allowRootAccess": AllowRootAccess = value == "True"; break;
            case "enableUnmappedAccess": EnableUnmappedAccess = value == "True"; break;
            case "sslPolicy": SslPolicy = value; break;
            case "authMode": AuthMode = value; break;
            case "port": if (int.TryParse(value, out var p) && p > 0) Port = p; break;
            case "anonymousEnabled": AnonymousEnabled = value == "True"; break;
        }
        RaiseAll();
    }

    /// <summary>TS form.resetFields() + nameTouchedRef=false（创建成功后复位）。</summary>
    public void Reset()
    {
        Protocol = "smb"; PresetId = null; Name = ""; Path = ""; Description = "";
        NameTouched = false;
        Encrypted = false; FullAccess = ""; ChangeAccess = "";
        ReadAccess = string.Join(",", ShareNameUtils.DefaultReadAccess);
        NoAccess = ""; EncryptData = false; ShareShadowCopy = false;
        FolderEnumerationMode = "Unrestricted"; CachingMode = "Manual"; ConcurrentUserLimit = 0;
        Authentication = ""; NfsPermission = "rw"; EnableUnmappedAccess = false;
        AllowRootAccess = false; AnonymousUid = 0; AnonymousGid = 0;
        Port = 21; SslPolicy = "SslAllow"; AuthMode = "basic"; AnonymousEnabled = false;
        RaiseAll();
    }

    static IReadOnlyList<string> SplitAccounts(string raw) =>
        raw.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(s => s.Length > 0)
           .ToList();

    /// <summary>
    /// TS handleCreate 的 CreateShareInput 组装（含新手模式收敛）：
    /// advancedMode=false 时隐藏高级项一律不提交；readAccess 取表单值
    /// （未展开访问控制时即初始默认 Users——DEFAULT_READ_ACCESS 补齐）。
    /// </summary>
    public CreateShareInput BuildCreateInput(bool advancedMode)
    {
        var proto = ProtocolExtForm.Parse(Protocol);
        var read = SplitAccounts(ReadAccess);
        IReadOnlyList<string>? Arr(string raw) { var v = SplitAccounts(raw); return v.Count > 0 ? v : null; }
        var auths = SplitAccounts(Authentication).Select(NfsAuthMethodExt.Parse).ToList();
        var input = new CreateShareInput
        {
            protocol = proto,
            name = Name,
            path = Path,
            description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            fullAccess = Arr(FullAccess),
            changeAccess = Arr(ChangeAccess),
            readAccess = read.Count > 0 ? read : null,
            noAccess = Arr(NoAccess),
            authentication = auths.Count > 0 ? auths : null,
            nfsPermission = proto == CoreProtocol.Nfs ? NfsPermissionExt.Parse(NfsPermission) : null,
            port = proto is CoreProtocol.Ftp or CoreProtocol.Webdav ? Port : null,
            sslPolicy = proto == CoreProtocol.Ftp ? SslPolicyExt.Parse(SslPolicy) : null,
            authMode = proto == CoreProtocol.Ftp ? AuthModeExt.Parse(AuthMode) : null,
            anonymousEnabled = proto == CoreProtocol.Webdav ? AnonymousEnabled : null,
        };
        if (advancedMode)
        {
            // 新手模式不渲染也不提交：残留值不得静默入库（ADVANCED_FORM_KEYS 全量键）
            input = input with
            {
                encrypted = proto == CoreProtocol.Smb ? Encrypted : null,
                encryptData = proto == CoreProtocol.Smb ? EncryptData : null,
                concurrentUserLimit = proto == CoreProtocol.Smb && ConcurrentUserLimit > 0 ? ConcurrentUserLimit : null,
                cachingMode = proto == CoreProtocol.Smb ? CachingModeExt.Parse(CachingMode) : null,
                folderEnumerationMode = proto == CoreProtocol.Smb ? FemExt.Parse(FolderEnumerationMode) : null,
                shareShadowCopy = proto == CoreProtocol.Smb ? ShareShadowCopy : null,
            };
        }
        return input;
    }

    /// <summary>SMB 创建参数（IShareService.CreateShareAsync 入参；同 BuildCreateInput 的 SMB 分支）。</summary>
    public CreateShareOpts BuildSmbCreateOpts(bool advancedMode) => new()
    {
        name = Name,
        path = Path,
        description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        fullAccess = SplitAccounts(FullAccess),
        changeAccess = SplitAccounts(ChangeAccess),
        readAccess = SplitAccounts(ReadAccess),
        noAccess = SplitAccounts(NoAccess),
        encrypted = advancedMode ? (bool?)Encrypted : null,
        encryptData = advancedMode ? (bool?)EncryptData : null,
        concurrentUserLimit = advancedMode && ConcurrentUserLimit > 0 ? ConcurrentUserLimit : null,
        cachingMode = advancedMode ? CachingModeExt.Parse(CachingMode) : null,
        folderEnumerationMode = advancedMode ? FemExt.Parse(FolderEnumerationMode) : null,
        shareShadowCopy = advancedMode ? ShareShadowCopy : null,
    };

    // ===== INPC =====
    void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Protocol)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresetId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Encrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptData)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShareShadowCopy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderEnumerationMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CachingMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConcurrentUserLimit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Authentication)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NfsPermission)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableUnmappedAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllowRootAccess)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnonymousUid)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnonymousGid)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Port)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SslPolicy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AuthMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnonymousEnabled)));
    }
}

/// <summary>
/// 编辑共享表单 ViewModel（对齐 Shares.tsx 编辑 Modal：名称/路径只读，
/// 按协议展示可改字段；root 访问属深水区——新手模式不渲染也不提交，
/// 隐藏值不得覆盖现网配置）。
/// </summary>
public sealed class ShareEditFormVm
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public Protocol Protocol { get; init; }

    public string Description { get; set; } = "";
    public string NfsPermission { get; set; } = "rw";
    public bool AllowRootAccess { get; set; }
    public string SslPolicy { get; set; } = "SslAllow";
    public string AuthMode { get; set; } = "basic";
    public bool AnonymousEnabled { get; set; }

    /// <summary>TS handleEdit 的 UpdateShareInput 组装（advancedMode 门控 allowRootAccess）。</summary>
    public UpdateShareInput BuildUpdateInput(bool advancedMode) => new()
    {
        protocol = Protocol,
        description = Description,
        nfsPermission = Protocol == Protocol.Nfs ? NfsPermissionExt.Parse(NfsPermission) : null,
        allowRootAccess = Protocol == Protocol.Nfs && advancedMode ? AllowRootAccess : null,
        sslPolicy = Protocol == Protocol.Ftp ? SslPolicyExt.Parse(SslPolicy) : null,
        authMode = Protocol == Protocol.Ftp ? AuthModeExt.Parse(AuthMode) : null,
        anonymousEnabled = Protocol == Protocol.Webdav ? AnonymousEnabled : null,
    };
}

// ===== 小写字符串值 → 契约枚举（TS 侧以字符串字面量传 IPC，WPF 表单以字符串绑定） =====
internal static class ProtocolExtForm
{
    public static Protocol Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "nfs" => Protocol.Nfs,
        "ftp" => Protocol.Ftp,
        "webdav" => Protocol.Webdav,
        _ => Protocol.Smb,
    };
}

static class NfsPermissionExt
    {
        public static NfsPermission Parse(string? s) =>
            string.Equals(s?.Trim().ToLowerInvariant(), "ro") ? NfsPermission.Ro : NfsPermission.Rw;
    }
static class SslPolicyExt
{
    public static SslPolicy Parse(string? s) => s switch
    {
        "SslRequire" => SslPolicy.SslRequire,
        "SslRequireCredentials" => SslPolicy.SslRequireCredentials,
        _ => SslPolicy.SslAllow,
    };
}
static class AuthModeExt
{
    public static FtpAuthMode Parse(string? s) => s switch
    {
        "anonymous" => FtpAuthMode.Anonymous,
        "windows" => FtpAuthMode.Windows,
        _ => FtpAuthMode.Basic,
    };
}
static class CachingModeExt
{
    public static CachingMode Parse(string? s) => s switch
    {
        "None" => CachingMode.None,
        "Documents" => CachingMode.Documents,
        "Programs" => CachingMode.Programs,
        "BranchCache" => CachingMode.BranchCache,
        _ => CachingMode.Manual,
    };
}
static class FemExt
{
    public static FolderEnumerationMode Parse(string? s) => s == "AccessBased" ? FolderEnumerationMode.AccessBased : FolderEnumerationMode.Unrestricted;
}
static class NfsAuthMethodExt
{
    public static NfsAuthMethod Parse(string? s) => s switch
    {
        "krb5" => NfsAuthMethod.Krb5,
        "krb5i" => NfsAuthMethod.Krb5i,
        "krb5p" => NfsAuthMethod.Krb5p,
        _ => NfsAuthMethod.Sys,
    };
}
