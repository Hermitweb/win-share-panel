using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;

namespace WinShare.App.Shares;

/// <summary>
/// 共享配置导入导出护栏（逐条对齐 electron/services/share.ts importConfig 的 R-1 护栏
/// 与 electron/lib/powershell.ts 校验白名单；纯逻辑，xUnit 直测）：
/// - 超大内容先行拒绝（2MB）、非法 JSON 友好报错、结构错误、条目上限 500
/// - 逐条目 name/path 白名单校验（对齐 validateName/validatePath 正则原文）
/// - 导入完成文案（成功/跳过 + 前 2 条原因 + "等"）
/// </summary>
public static class ShareImportGuard
{
    /// <summary>TS: 导入 JSON 上限 2MB（先拒超大再 parse）。</summary>
    public const int MaxJsonBytes = 2 * 1024 * 1024;

    /// <summary>TS: 导入条目上限 500（超限要求拆分分批）。</summary>
    public const int MaxEntries = 500;

    /// <summary>TS validateName(name, maxLen=80)：^[A-Za-z0-9._\-\u4e00-\u9fa5 ]{1,80}$。</summary>
    public static bool ValidateName(string? name, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLen) return false;
        // 正则原文（verbatim 内 \uXXXX 由引擎解释）：^[A-Za-z0-9._\-\u4e00-\u9fa5 ]{1,maxLen}$
        return Regex.IsMatch(name, $"^[A-Za-z0-9._\\-\\u4e00-\\u9fa5 ]{{1,{maxLen}}}$");
    }

    /// <summary>TS validateShareName：共享名（允许尾 $ 后缀，单段 1..79 字符 + 可选 $）。</summary>
    public static bool ValidateShareName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80) return false;
        return Regex.IsMatch(name, @"^[A-Za-z0-9._\u4e00-\u9fa5 -]{1,79}\$?$");
    }

    /// <summary>TS validateAccountName：账号名（放行 DOMAIN\user、Everyone、机器$ 等合法形态）。</summary>
    public static bool ValidateAccountName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 120) return false;
        return Regex.IsMatch(name, @"^[A-Za-z0-9._\-\u4e00-\u9fa5 \\$]+$");
    }

    /// <summary>
    /// TS validatePath 正则原文：^[A-Za-z]:[\\/]([^\u0000-\u001f&lt;&gt;:"|?*][^&lt;&gt;:"|?]*)*$
    /// （本地盘路径：首段字符排除控制字符与 &lt;&gt;:"|?*，后续段排除 &lt;&gt;:"|?）。
    /// </summary>
    public static bool ValidatePath(string? p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        const string pattern =
            "^[A-Za-z]:[\\\\/](" +          // 盘符 + 首部分隔符
            "[^\\u0000-\\u001f<>:\"|?*]" +  // 段首字符：排除控制字符与 <>:"|?*
            "[^<>:\"|?]*" +                 // 段内其余字符：排除 <>:"|?
            ")*$";                           // 允许多段，锚定结尾
        return Regex.IsMatch(p, pattern);
    }

    /// <summary>
    /// TS importConfig 的服务调用前护栏：依次检查 类型/2MB 上限/JSON 解析/shares 数组/500 条目上限。
    /// 违规抛 ArgumentException，消息与 TS Errors.invalidParam 文案逐字一致。
    /// 返回解析后的 shares 条目数组（供逐条处理）。
    /// </summary>
    public static JsonElement GuardImportJson(string? json)
    {
        if (json is null)
            throw new ArgumentException("导入内容不合法或超过 2MB 上限");
        if (json.Length > MaxJsonBytes)
            throw new ArgumentException("导入内容不合法或超过 2MB 上限");

        JsonElement data;
        try
        {
            data = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            throw new ArgumentException("导入文件格式错误：JSON 解析失败");
        }

        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("shares", out var shares) ||
            shares.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("导入文件格式错误");
        }
        if (shares.GetArrayLength() > MaxEntries)
        {
            throw new ArgumentException($"导入条目超过 {MaxEntries} 上限，请拆分后分批导入");
        }
        return shares;
    }

    /// <summary>
    /// 单条目合法性（TS importConfig 逐条 validateName/validatePath）：
    /// 非法条目跳过并计入 errors，不中断整体导入。
    /// </summary>
    public static bool IsValidEntry(string? name, string? path) =>
        ValidateName(name) && ValidatePath(path);

    /// <summary>
    /// TS 导入完成文案（skip&gt;0 走 warning，附前 2 条失败原因 + "等"）：
    /// 与 Shares.tsx handleImport 逐字一致。
    /// </summary>
    public static string BuildImportReportText(ImportReport report)
    {
        if (report.skipped > 0)
        {
            var top = string.Join("；", report.errors.Take(2));
            return $"导入完成：成功 {report.imported} 个，跳过 {report.skipped} 个。{top}{(report.errors.Count > 2 ? " 等" : "")}";
        }
        return $"导入完成：成功 {report.imported} 个共享（仅 SMB）";
    }
}
