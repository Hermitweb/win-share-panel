using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Settings;

/// <summary>
/// 开机自启卡（登录快捷方式读/写系统真实值 + 持久化用户意愿）。
/// [Source: src/components/AppSettingsCards.tsx AutoStartCard L131-200]
/// 语义对齐：
///  - 写后读回真实值为准（系统可能拒绝而未生效 → 文案 warning「系统未接受该设置」）；
///  - 读回值写入 appstate.autoStart 记录用户意愿（store 失败 → warning「偏好保存失败」）；
///  - 写入失败 → 目标态作废、重读真实值（开关回到进入页面时的状态）、原因可见；
///  - 平台不支持（服务缺席）→ 整卡降级（Unavailable 状态 + 说明，不给改不动的开关）。
/// C# 侧数据源：IPathRegistrationService 的登录快捷方式（setLoginShortcut/getLoginShortcut）。
/// </summary>
public sealed class AutoStartCardViewModel : SettingsCardBase
{
    readonly IPathRegistrationService? _svc;
    readonly AppSettingsStore _store;

    public bool Unavailable { get; private set; }
    public string? UnavailableNote { get; private set; }
    public bool Reading { get; private set; }

    /// <summary>系统登录项真实值（读取成功前为 null）。</summary>
    public bool? Actual { get; private set; }

    /// <summary>乐观目标态（写入中非 null）。</summary>
    public bool? Pending { get; private set; }

    public AutoStartCardViewModel(IPathRegistrationService? service, AppSettingsStore store)
    {
        _svc = service;
        _store = store;
        if (service is null)
        {
            Unavailable = true;
            UnavailableNote = "开机自启管理不可用（PATH/登录项服务未注册）：仅显示保存的用户意愿。";
        }
        Actual = store.Load().autoStart;
        Raise();
    }

    /// <summary>界面当前展示值（对齐 TS <c>pending ?? data ?? false</c>）。</summary>
    public bool Shown => Pending ?? Actual ?? false;

    public string StateText => Actual switch
    {
        null when Unavailable => "意愿：" + (Shown ? "已开启" : "已关闭"),
        null => "读取中…",
        true => "已开启",
        false => "已关闭",
    };

    public Task<string?> ReadAsync()
    {
        if (_svc is null)
        {
            Actual = _store.Load().autoStart;
            Raise();
            return Task.FromResult<string?>(null);
        }
        if (Busy) return Task.FromResult<string?>(null);
        Busy = true;
        Reading = true;
        var t = Task.Run(async () =>
        {
            try
            {
                Actual = await _svc.GetLoginShortcutAsync();
                Success = null;
                return null;
            }
            catch (Exception e)
            {
                Error = $"读取开机自启状态失败：{e.Message}";
                return e.Message;
            }
            finally
            {
                Busy = false;
                Reading = false;
                Raise();
            }
        });
        return t;
    }

    /// <summary>
    /// 写入登录项 + 持久化意愿（对齐 TS write：成功/未接受/失败三分支文案逐字对齐）。
    /// 返回 null=成功，否则=失败文案。
    /// </summary>
    public Task<string?> WriteAsync(bool next)
    {
        if (_svc is null) return Task.FromResult<string?>("开机自启管理不可用：PATH/登录项服务未注册");
        if (Pending is not null) return Task.FromResult<string?>(null);
        if (Busy) return Task.FromResult<string?>(null);
        Pending = next;
        Raise();
        var t = Task.Run(async () =>
        {
            try
            {
                await _svc.SetLoginShortcutAsync(next);
                // 主进程返回后读回真实值，以其为准（可能被组策略拒绝而未生效）
                var actual = await _svc.GetLoginShortcutAsync();
                Actual = actual;
                Pending = null;
                if (actual != next)
                {
                    Success = null;
                    Error = $"系统未接受该设置，当前为{(actual ? "已开启" : "已关闭")}";
                }
                else
                {
                    Success = actual
                        ? "已开启开机自启（下次登录 Windows 时自动启动本面板）"
                        : "已关闭开机自启";
                    Error = null;
                }
                try
                {
                    // 记录用户意愿：启动时据此同步登录项
                    _store.SaveState(new AppStatePatch { autoStart = actual });
                }
                catch (Exception e)
                {
                    Error = $"开机自启已生效，但偏好保存失败（下次启动可能回到系统状态）：{e.Message}";
                }
                Raise();
                return null;
            }
            catch (Exception e)
            {
                Pending = null;
                Error = $"开机自启设置失败：{e.Message}";
                Raise();
                return e.Message;
            }
        });
        return t;
    }
}
