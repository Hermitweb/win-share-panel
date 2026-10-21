// 移植自 electron/lib/url.ts。
using System.Text.RegularExpressions;

namespace WinShare.Core.Lib;

/// <summary>
/// 外部链接白名单（TS: url.ts isSafeExternalUrl）。
/// system:openExternal 会把 URL 交给系统默认浏览器/shell；渲染层不可信，
/// 只放行显式 http/https 起始、限制长度、不 trim（前导空白会改变 shell 解析语义）。
/// </summary>
public static class UrlGuard
{
    /// <summary>TS: MAX_URL_LENGTH。</summary>
    public const int MaxUrlLength = 2000;

    private static readonly Regex Safe = new(@"^https?://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// TS: isSafeExternalUrl(url: unknown)。C# 以 string? 承载 unknown：null 对应 TS 的非字符串拒绝分支。
    /// </summary>
    public static bool IsSafeExternalUrl(string? url)
    {
        if (url is null) return false;
        if (url.Length == 0 || url.Length > MaxUrlLength) return false;
        return Safe.IsMatch(url);
    }
}
