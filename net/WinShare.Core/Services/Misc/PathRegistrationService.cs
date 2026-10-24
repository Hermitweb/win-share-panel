// 移植自 electron/services/pathRegistration.ts（系统注册项一键管理：用户级 PATH / 登录快捷键 / 服务待处理标记）。
// 实现选择同 TS：
//  - 读写 HKCU\Environment\Path（用户级 PATH，不需要 UAC 提权）
//  - 幂等：写入前读现存 PATH，仅当不含本应用目录精确匹配时追加；撤销时仅当含精确匹配时删除
//  - 广播：用户级 PATH 变更广播 WM_SETTINGCHANGE（PowerShell NativeMethods，经 PoolSingleton；PS 缺失时静默降级）
// PS 命令串从 TS 原文件逐字复制。electron 的 app.isPackaged/resourcesPath 在 C# 侧以
// isPackaged + appDir 构造参数承载（dev 模式写操作被拦截，与 TS 等价）。
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Misc;

public sealed class PathRegistrationService : IPathRegistrationService
{
    /// <summary>TS: HKCU_RUN_KEY（与 installer-hooks.nsh 对齐）。</summary>
    private const string HkcuRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 读取用户级 PATH 的 PS 命令（TS 原串，单引号内反斜杠按字面保留）。
    /// </summary>
    private const string GetUserPathCommand =
        "(Get-ItemProperty -Path 'HKCU:\\Environment' -Name Path -ErrorAction SilentlyContinue).Path";

    /// <summary>清除用户级 PATH（value 为空串时）。</summary>
    private const string RemoveUserPathCommand =
        "Remove-ItemProperty -Path 'HKCU:\\Environment' -Name Path -ErrorAction SilentlyContinue";

    /// <summary>
    /// 广播 WM_SETTINGCHANGE（环境变量段）的 PS 命令（TS 原串，含 Add-Type P/Invoke；
    /// 原命令以换行起头，保持逐字）。
    /// </summary>
    private const string BroadcastCommand = "\n" +
"""
 $sig = @'
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, string lp, uint fu, uint to, out IntPtr r);
}
'@
Add-Type -TypeDefinition $sig
$r = [IntPtr]::Zero
[void]([W]::SendMessageTimeout([IntPtr]([int]0xFFFF), 0x1A, [IntPtr]::Zero, 'Environment', 0x2, 1000, [ref]$r))
""";

    private readonly string? _appDir;
    private readonly bool _isPackaged;
    private readonly IPsInvoker _ps;

    /// <summary>
    /// TS: currentUserPathDir() = app.isPackaged ? dirname(process.resourcesPath) : app.getAppPath()。
    /// C# 由宿主注入：appDir = 本应用目录（打包版 resourcesPath 的父目录）；isPackaged 对应 app.isPackaged。
    /// </summary>
    public PathRegistrationService(string? appDir, bool isPackaged, IPsInvoker? ps = null)
    {
        _appDir = appDir;
        _isPackaged = isPackaged;
        _ps = ps ?? PsRunner.Default;
    }

    /// <summary>TS: getUserPath()（HKCU\Environment\Path 读取；缺失/PS 失败返回空串）。</summary>
    public async Task<string> GetUserPathAsync()
    {
        try
        {
            // PS 单引号串内无插值/转义语义，反斜杠按字面保留 —— 与 powershell.ts 的 psQuote 约定一致
            JsonElement raw = await _ps.RunJsonAsync(GetUserPathCommand, 10000, 0);
            return raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>TS: setUserPath(value)（写入用户级 PATH；空串表示清除）。</summary>
    public async Task SetUserPathAsync(string value)
    {
        if (value == "")
        {
            await _ps.RunVoidAsync(RemoveUserPathCommand, 10000, 0);
            return;
        }
        // 值用单引号包裹 + 内部 ' 转义为 ''（PsArgs.PsQuote 同一规则）；PATH 值里的反斜杠按字面保留
        string psQuoted = PsArgs.PsQuote(value);
        await _ps.RunVoidAsync($"Set-ItemProperty -Path 'HKCU:\\Environment' -Name Path -Value {psQuoted} -Type String", 10000, 0);
    }

    /// <summary>TS: broadcastPathChange()（广播 WM_SETTINGCHANGE；PS 缺失时静默降级为 false）。</summary>
    public async Task<bool> BroadcastPathChangeAsync()
    {
        try
        {
            await _ps.RunVoidAsync(BroadcastCommand, 15000, 0);
            return true;
        }
        catch (Exception e)
        {
            Logger.Warn("pathReg", $"[broadcastPathChange] 广播失败（PS 不可用或权限不足）:", e.Message);
            return false;
        }
    }

    /// <summary>TS: registerAppInUserPath()（把本应用目录追加到用户级 PATH，幂等）。</summary>
    public async Task<PathRegistration> RegisterAppInUserPathAsync()
    {
        string dir = _appDir ?? "";
        if (!_isPackaged)
        {
            return new PathRegistration
            {
                registered = false,
                pathLength = 0,
                message = "开发模式不支持一键注册 PATH；请使用打包版",
                broadcast = false,
            };
        }
        string before = await GetUserPathAsync();
        if (SegmentMatch(dir).IsMatch(before))
        {
            return new PathRegistration
            {
                registered = true,
                pathLength = before.Length,
                message = $"PATH 已包含 {dir}，无需重复注册",
                broadcast = false,
            };
        }
        string appended = before.Length > 0 ? $"{before};{dir}" : dir;
        await SetUserPathAsync(appended);
        bool broadcast = await BroadcastPathChangeAsync();
        return new PathRegistration
        {
            registered = true,
            pathLength = appended.Length,
            message = broadcast
                ? $"已将 {dir} 追加到用户 PATH，并广播给本会话进程"
                : $"已将 {dir} 追加到用户 PATH；广播失败，需重开终端/登录才生效",
            broadcast = broadcast,
        };
    }

    /// <summary>TS: unregisterAppFromUserPath()（从用户级 PATH 移除本应用目录，幂等）。</summary>
    public async Task<PathRegistration> UnregisterAppFromUserPathAsync()
    {
        string dir = _appDir ?? "";
        if (!_isPackaged)
        {
            return new PathRegistration
            {
                registered = false,
                pathLength = 0,
                message = "开发模式不支持撤销 PATH 注册",
                broadcast = false,
            };
        }
        string before = await GetUserPathAsync();
        if (!SegmentMatch(dir).IsMatch(before))
        {
            return new PathRegistration
            {
                registered = false,
                pathLength = before.Length,
                message = $"PATH 未包含 {dir}，无需撤销",
                broadcast = false,
            };
        }
        // 拆分 + 过滤 + 重组；保留引号（若有）
        List<string> segs = before.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        string norm = EscapeRegExpLiteral(dir);
        // 段本身可能带引号（PATH 里带空格的目录惯例写法），故两侧各允许一个可选引号。
        var keepPattern = new Regex($"^\"?{norm}\"?$", RegexOptions.IgnoreCase);
        var kept = segs.Where(s => !keepPattern.IsMatch(s)).ToList();
        await SetUserPathAsync(string.Join(";", kept));
        bool broadcast = await BroadcastPathChangeAsync();
        return new PathRegistration
        {
            registered = false,
            // 对齐 TS：此处 kept 是数组，.length 为段数（非字符串长度）
            pathLength = kept.Count,
            message = broadcast
                ? $"已从用户 PATH 移除 {dir}，并广播给本会话进程"
                : $"已从用户 PATH 移除 {dir}；广播失败，需重开终端/登录才生效",
            broadcast = broadcast,
        };
    }

    /// <summary>TS: isAppInUserPath()（查询当前用户级 PATH 是否已注册本应用目录）。</summary>
    public async Task<bool> IsAppInUserPathAsync()
    {
        string dir = _appDir ?? "";
        string p = await GetUserPathAsync();
        if (p.Length == 0) return false;
        return SegmentMatch(dir).IsMatch(p);
    }

    /// <summary>
    /// TS: setLoginShortcut(enabled)（登录快速启动 HKCU Run 键，与 NSIS 安装器钩子 ② 等价；
    /// 返回说明文本）。
    /// </summary>
    public async Task<string> SetLoginShortcutAsync(bool enabled)
    {
        string dir = _appDir ?? "";
        if (!_isPackaged)
        {
            return "开发模式不支持补写 HKCU Run 键；请重新安装或手动写注册表";
        }
        // 键名/值名用单引号字面量（PS 单引号串内无插值，反斜杠按字面保留）
        string keyPath = $"HKCU:{HkcuRunKey}";
        if (enabled)
        {
            // TS: const cmd = `"${dir}\\WinShare Panel.exe" --silent`（字面单反斜杠）
            string cmd = "\"" + dir + @"\" + "WinShare Panel.exe" + "\" --silent";
            await _ps.RunVoidAsync(
                $"Set-ItemProperty -Path '{keyPath}' -Name WinSharePanel -Value {PsArgs.PsQuote(cmd)} -Type String",
                10000, 0);
            return $"已写入登录快速启动：{keyPath}\\WinSharePanel";
        }
        await _ps.RunVoidAsync(
            $"Remove-ItemProperty -Path '{keyPath}' -Name WinSharePanel -ErrorAction SilentlyContinue",
            10000, 0);
        return $"已删除登录快速启动：{keyPath}\\WinSharePanel";
    }

    /// <summary>TS: getLoginShortcut()（登录快速启动是否启用）。</summary>
    public async Task<bool> GetLoginShortcutAsync()
    {
        try
        {
            string keyPath = $"HKCU:{HkcuRunKey}";
            JsonElement raw = await _ps.RunJsonAsync(
                $"(Get-ItemProperty -Path '{keyPath}' -Name WinSharePanel -ErrorAction SilentlyContinue).WinSharePanel",
                10000, 0);
            return raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TS: readServicePendingMarker()（读取 NSIS 安装器钩子 ③ 写下的"Windows 服务预留标记"；
    /// 标记缺失（portable 安装或老版本升级）时返回 false；PS 调用抛错静默降级为 false）。
    /// </summary>
    public async Task<bool> ReadServicePendingMarkerAsync()
    {
        try
        {
            JsonElement raw = await _ps.RunJsonAsync(
                "(Get-ItemProperty -Path 'HKLM:Software\\WinSharePanel' -Name ServicePending -ErrorAction SilentlyContinue).ServicePending",
                10000, 0);
            // TS: String(raw) === '1'（数值 1 与字符串 '1' 均成立）
            return raw.ValueKind switch
            {
                JsonValueKind.String => raw.GetString() == "1",
                JsonValueKind.Number => raw.GetInt32() == 1,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TS: escapeRegExpLiteral()（同字符集同规则）。
    /// 把路径转成正则**字面量**：反斜杠与正则元字符（. * + ? ^ $ { } ( ) | [ ]）一并转义。
    /// 不能只做 Replace(@"\", @"\\") —— 那样路径里的 `(x86)` 仍会被当成捕获组，
    /// 真实段匹配不上，反而匹配到形似但不同的 `C:\Program Files x86\...`，
    /// 导致"撤销 PATH"移除错段、留下真段。
    /// </summary>
    private static string EscapeRegExpLiteral(string s) =>
        Regex.Replace(s, @"[.*+?^${}()|\[\]\\]", @"\$&");

    /// <summary>
    /// TS: new RegExp(`(^|;)\s*"?{dir}"?\s*(?=;|$)`, 'i')（dir 先经 escapeRegExpLiteral 转义）。
    /// </summary>
    private static Regex SegmentMatch(string dir) =>
        new($"(^|;)\\s*\"?(?:{EscapeRegExpLiteral(dir)})\"?\\s*(?=;|$)", RegexOptions.IgnoreCase);
}
