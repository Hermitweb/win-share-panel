using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinShare.Core.Ps;

/// <summary>
/// PowerShell 命令安全闸门 + -EncodedCommand 编码助手（electron/lib/powershell.ts 安全拼接面 C# 移植）。
/// IPC 边界无运行时类型保证：数值/布尔/枚举字段拼入 PowerShell 脚本前必须先过本类闸门
/// （非法值返回 null，调用方跳过该字段），杜绝字符串拼接注入；脚本统一以 UTF-16LE Base64 EncodedCommand 下发。
/// </summary>
public static class PsArgs
{
    // === 编码：-EncodedCommand 语义（恒定按 UTF-16LE 解码，规避 GBK 代码页破坏中文）===

    /// <summary>TS: encodeCommand — Base64(UTF-16LE)。</summary>
    public static string EncodeCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    /// <summary>载荷 = encodeCommand(mode + NUL 分隔符 + command)；NUL 不会出现在合法命令中。</summary>
    public static string BuildPayload(string mode, string command) =>
        EncodeCommand(mode + "\u0000" + command);

    // === 引用 / 转义 ===

    /// <summary>TS: psQuote — 单引号包裹值，内部 ' 转义为 ''；PS 单引号串内 $ 无变量插值语义。</summary>
    public static string PsQuote(object? value)
    {
        string s = value switch
        {
            null => "",
            string str => str,
            _ => value.ToString() ?? "",
        };
        return "'" + s.Replace("'", "''") + "'";
    }

    /// <summary>
    /// TS: psEscapeSingle — 单引号字符串内部转义（不包裹外层引号），
    /// 用于将值嵌入更大的单引号字符串中，如 IIS 路径 'IIS:\Sites\{name}'。
    /// </summary>
    public static string PsEscapeSingle(object? value) =>
        (value switch
        {
            null => "",
            string str => str,
            _ => value.ToString() ?? "",
        }).Replace("'", "''");

    // === 名称 / 账号 / 路径白名单 ===

    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9._\-\u4e00-\u9fa5 ]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex ShareNameRegex = new(@"^[A-Za-z0-9._\u4e00-\u9fa5 -]{1,79}\$?$", RegexOptions.Compiled);
    private static readonly Regex AccountNameRegex = new(@"^[A-Za-z0-9._\-\u4e00-\u9fa5 \\$]+$", RegexOptions.Compiled);
    private static readonly Regex PathRegex = new(@"^[A-Za-z]:[\\/]([^\u0000-\u001f<>:""|?*][^<>:""|?*]*)*$", RegexOptions.Compiled);

    /// <summary>
    /// TS: validateName — 共享名/账号名白名单校验。
    /// 不允许 $：Windows 本地账号名不会在中间含 $，而 $ 在 PowerShell 双引号串中触发变量插值，存在注入风险。
    /// </summary>
    public static bool ValidateName(string? name, int maxLen = 80) =>
        !string.IsNullOrEmpty(name) && name.Length <= maxLen && NameRegex.IsMatch(name);

    /// <summary>
    /// TS: validateShareName — 共享名「读取/操作」校验：放行 Windows 系统/隐藏共享的合法尾缀 "$"（IPC$、ADMIN$、C$）。
    /// 安全性：仅允许结尾一个 $，中间 $ 仍拒（拒绝 $( 子表达式注入形态）；
    /// 创建/写入路径（createShare、用户名、组名）继续使用严格的 ValidateName。
    /// </summary>
    public static bool ValidateShareName(object? name) =>
        name is string s && s.Length > 0 && s.Length <= 80 && ShareNameRegex.IsMatch(s);

    /// <summary>
    /// TS: validateAccountName — A1 域账号支持：共享名校验字符集上额外放行反斜杠
    /// （DOMAIN\user、NT AUTHORITY\SYSTEM、WORKGROUP\machine$）与任意位置 $，长度 ≤ 120。
    /// 账户名仅经 psQuote 单引号上下文嵌入，单引号串内无插值/转义语义。
    /// </summary>
    public static bool ValidateAccountName(object? name) =>
        name is string s && s.Length > 0 && s.Length <= 120 && AccountNameRegex.IsMatch(s);

    /// <summary>TS: validatePath — Windows 绝对路径校验（控制字符、&lt;&gt;:"|?* 排除）。</summary>
    public static bool ValidatePath(string? p) => p is not null && PathRegex.IsMatch(p);

    // === 类型闸门：非法值返回 null（调用方跳过该字段），杜绝布尔/数值/枚举字段被用作注入载体 ===

    /// <summary>TS: psBool — 仅接受真正的 boolean，输出 $true/$false。</summary>
    public static string? PsBool(object? v) => v is bool b ? (b ? "$true" : "$false") : null;

    /// <summary>TS: psNumber — 仅接受有限数字，输出其字符串形式；NaN/Infinity/非数字类型返回 null。</summary>
    public static string? PsNumber(object? v)
    {
        switch (v)
        {
            case null:
            case bool:
            case string:
                return null;
            case double d when double.IsNaN(d) || double.IsInfinity(d):
            case float f when float.IsNaN(f) || float.IsInfinity(f):
            case decimal m when !(m > 0m || m < 0m || m == 0m): // NaN / ±Infinity（decimal 无 IsNaN API）
                return null;
            case IConvertible c:
                return Convert.ToString(c, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    /// <summary>TS: psEnum — 仅接受白名单内的字符串，输出裸值（调用方需确保 cmdlet 接受裸枚举）。</summary>
    public static string? PsEnum(object? v, IReadOnlySet<string>? allowed) =>
        v is string s && allowed is not null && allowed.Contains(s) ? s : null;

    /// <summary>
    /// TS: firstOrNull — 单值期望查询归一化：JSON 解析对「无结果」输出返回空数组（在 TS 里是 truthy！）。
    /// 存在性检查必须经本函数取首元素，否则条件恒真（B-2 附带发现的同型缺陷）。
    /// </summary>
    public static object? FirstOrNull(object? value)
    {
        if (value is System.Collections.IList list)
        {
            return list.Count > 0 ? list[0] : null;
        }
        return value;
    }
}

