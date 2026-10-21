using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 一键诊断卡（排障入口；逐项容错：单探测失败记 warn 不崩整体——契约语义）。
/// [Source: electron/services/diagnose.ts 契约 IDiagnoseService + Settings.tsx 诊断入口装配探测]
/// 服务缺席时入口禁用并写明原因（对齐 TS「诊断面板尚未合入」的 glob 探测降级文案模式），
/// 不留下「点了没反应」的入口。
/// </summary>
public sealed class DiagnoseViewModel : SettingsCardBase
{
    readonly IDiagnoseService? _svc;

    public DiagnoseViewModel(IDiagnoseService? service)
    {
        _svc = service;
        if (service is null)
        {
            DegradedNote = "诊断服务未注册（等待 Core 服务层实现）：一键诊断暂不可用，入口保留待服务就绪";
        }
        Raise();
    }

    public bool Available => _svc is not null;
    public string? DegradedNote { get; private set; }
    public IReadOnlyList<DiagnoseItem> Items { get; private set; } = Array.Empty<DiagnoseItem>();
    /// <summary>结果面变化（视图重建列表用）。</summary>
    public event Action? Changed;
    public string? LastFixText { get; private set; }
    public bool Running { get; private set; }

    public async Task RunAsync(string? shareName = null)
    {
        if (_svc is null) return;
        if (Running) return;
        Running = true;
        Error = null;
        Raise(nameof(Running));
        try
        {
            Items = (await _svc.RunDiagnoseAsync(shareName is null ? null : new DiagnoseOptions { shareName = shareName }))
                .ToList();
            Success = "诊断完成";
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Error = $"诊断失败：{e.Message}";
        }
        finally
        {
            Running = false;
            Raise(nameof(Running));
        }
    }

    /// <summary>执行自动修复（契约：fix 存在时 UI 呈现修复按钮；成功返回说明文本）。</summary>
    public async Task<string?> ApplyFixAsync(DiagnoseFixKind fix, string? shareName = null)
    {
        if (_svc is null) return "诊断服务未注册，无法执行修复";
        try
        {
            LastFixText = await _svc.ApplyFixAsync(fix, shareName is null ? null : new DiagnoseOptions { shareName = shareName });
            Raise(nameof(LastFixText));
            Changed?.Invoke();
            return LastFixText;
        }
        catch (Exception e)
        {
            Error = $"修复失败：{e.Message}";
            return e.Message;
        }
    }

    /// <summary>状态 → (标签, 色名)：pass=通过(green) / warn=警告(orange) / fail=失败(red)。</summary>
    public static (string Label, string Color) StatusInfo(DiagnoseStatus s) => s switch
    {
        DiagnoseStatus.Pass => ("通过", "green"),
        DiagnoseStatus.Warn => ("警告", "orange"),
        DiagnoseStatus.Fail => ("失败", "red"),
        _ => ("警告", "orange"),
    };
}

/// <summary>
/// 版本适配卡（运行时 OS 功能探测展示；配置页按结果降级——如 SMB QUIC 不支持时禁用开关）。
/// [Source: src/components/OsCompatCard.tsx + ISystemService.GetOsInfoAsync]
/// </summary>
public sealed class OsCompatCardViewModel : SettingsCardBase
{
    readonly ISystemService? _sys;
    public OsInfo? Os { get; private set; }

    public OsCompatCardViewModel(ISystemService? system)
    {
        _sys = system;
        if (system is null)
        {
            DegradedNote = "系统服务未注册：OS 功能探测暂不可用";
        }
        Raise();
    }

    public string? DegradedNote { get; private set; }

    public bool QuicSupported => Os?.features.smbQuicConfig ?? true;
    public string? QuicHint => QuicSupported
        ? null
        : "需 Server 2022 / Win11 23H2+；本机探测不支持，保存时将自动跳过";

    public async Task LoadAsync()
    {
        if (_sys is null) return;
        try
        {
            Os = await _sys.GetOsInfoAsync(new OsInfoOptions { refresh = false });
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }
}
