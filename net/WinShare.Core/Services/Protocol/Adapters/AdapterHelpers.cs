// 三个协议适配器共用的 PS 回读归一化助手。
// TS 适配器里直接写 `Array.isArray(raw) ? raw : [raw]`、`!!v`、`if (!exists)`；
// C# 的 JsonElement 没有隐式归一化，且 `!v` 是 JS 真值表（''/0/false/null 为假），
// 故集中到本类，避免三份重复实现（NFS/FTP/WebDAV 均有同一形态）。
using System.Text.Json;
using WinShare.Core.Ps;
using WinShare.Core.Services.Shares; // PsGate:firstOrNull（与 TS 调用点同名）

namespace WinShare.Core.Services.Protocols.Adapters;

internal static class AdapterHelpers
{
    /// <summary>TS: Array.isArray(raw) ? raw : [raw]（JSON 模式无输出时为 Undefined → 空序列）。</summary>
    public static List<JsonElement> Items(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Array => raw.EnumerateArray().Select(e => e.Clone()).ToList(),
        JsonValueKind.Undefined or JsonValueKind.Null => new List<JsonElement>(),
        _ => new List<JsonElement> { raw.Clone() },
    };

    /// <summary>TS: firstOrNull(v)——空数组归一为「无结果」（JSON 模式对无结果输出 []，[] 在 JS 里是 truthy）。</summary>
    public static JsonElement? FirstOrNull(JsonElement raw) => PsGate.FirstOrNull(raw);

    /// <summary>TS: !!v（JS 真值表）——存在性判断不能用 `!=` 默认值代替，否则 ''/0 会被误判为存在。</summary>
    public static bool JsTruthy(JsonElement? el)
    {
        if (el is not JsonElement v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            _ => true,
        };
    }

    /// <summary>TS: iisPath(name) —— `'IIS:\Sites\{psEscapeSingle(name)}'`（单引号包裹，防 $/反引号插值）。</summary>
    public static string IisPath(string name) => "'IIS:\\Sites\\" + PsArgs.PsEscapeSingle(name) + "'";
}
