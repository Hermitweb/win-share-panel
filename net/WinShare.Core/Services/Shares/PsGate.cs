// TS: electron/lib/powershell.ts 的 psBool/psNumber/psEnum 在 TS 测试中以本地 mock 复刻
// （validateName: /^[A-Za-z0-9._\u4e00-\u9fa5 -]{1,80}$/ 等）。生产侧这些闸门已归一到
// Ps/PsArgs.cs（PsArgs.PsBool/PsNumber/PsEnum + ValidateName/ValidateShareName/ValidateAccountName/ValidatePath），
// 本类仅为服务域提供「与 TS 调用点同名」的窄门面，避免每处重复写 WinShare.Core.Ps 限定。
// 语义 1:1 对齐 powershell.ts：非法值返回 null（调用方跳过该字段）。
using System.Text.Json;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Shares;

/// <summary>
/// 注入闸门窄门面（TS: electron/lib/powershell.ts 的 psBool/psNumber/psEnum）。
/// 所有拼进 PowerShell 的用户输入必须过本类或 <see cref="PsArgs"/>，语义与 TS 版完全一致。
/// </summary>
internal static class PsGate
{
    /// <summary>TS: psBool —— 仅接受真正的 boolean，输出 $true/$false。</summary>
    public static string? PsBool(bool? v) => v is bool b ? PsArgs.PsBool(b) : null;

    /// <summary>TS: psNumber —— 仅接受有限数字（NaN/Infinity/非数字返回 null）。</summary>
    public static string? PsNumber(object? v) => PsArgs.PsNumber(v);

    /// <summary>TS: psEnum —— 白名单校验（CachingMode/FolderEnumerationMode/RequestCompression）。</summary>
    public static string? PsEnum(object? v, IReadOnlySet<string> allowed) => PsArgs.PsEnum(v, allowed);

    /// <summary>TS: psQuote —— 单引号包裹 + 内部 ' 转义为 ''。</summary>
    public static string PsQuote(object? v) => PsArgs.PsQuote(v);

    /// <summary>TS: validateName（创建路径严格校验：不允许 $）。</summary>
    public static bool ValidateName(string? v, int maxLen = 80) => PsArgs.ValidateName(v, maxLen);

    /// <summary>TS: validateShareName（读取/操作路径：放行尾 $）。</summary>
    public static bool ValidateShareName(object? v) => PsArgs.ValidateShareName(v);

    /// <summary>TS: validateAccountName（放行 DOMAIN\user 与任意位置 $）。</summary>
    public static bool ValidateAccountName(object? v) => PsArgs.ValidateAccountName(v);

    /// <summary>TS: validatePath。</summary>
    public static bool ValidatePath(string? v) => PsArgs.ValidatePath(v);

    /// <summary>
    /// TS: firstOrNull —— 单值期望查询归一化（JSON 模式对无结果输出空数组，空数组是 truthy！）。
    /// </summary>
    public static JsonElement? FirstOrNull(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return el.GetArrayLength() > 0 ? el[0].Clone() : null;
        }
        return el;
    }
}
