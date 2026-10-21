// System 服务域共用的 JS（TypeScript）语义兼容助手。
// TS 源大量依赖 JS 的宽松数值/数组语义（Number(undefined)=NaN、Array.isArray、Math.round 半进位、
// Date.parse 宽松解析），C# 侧不引入全局 using 改动，集中在本文件内复刻这些行为，
// 保证移植代码与 TS 逐行为对齐而不必在每个调用点重复防御。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinShare.Core.Services.Host;

/// <summary>JS 语义兼容助手（仅 System 域内部使用）。</summary>
internal static partial class SystemJsCompat
{
    /// <summary>TS: Number.isFinite(v) —— 只接受有限数值。</summary>
    public static bool IsFiniteNumber(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>
    /// TS: Number(value) 语义——把任意值转成数值：非数值/undefined/null/bool/数组对象一律 NaN
    /// （JS Number(true)=1、Number([])=0、Number('abc')=NaN；本域只需区分"有限正数"与"其他"，
    ///  故布尔/对象统一按 NaN 处理，与磁盘水位/端口计数的实际取值域一致）。
    /// </summary>
    public static double NumberOf(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out double d) ? d : double.NaN;
            case JsonValueKind.String:
                // TS: Number('12')=12、Number('')=0、Number('abc')=NaN
                string s = e.GetString() ?? "";
                if (s.Trim().Length == 0) return 0;
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? p : double.NaN;
            default:
                return double.NaN;
        }
    }

    /// <summary>TS: Number(v) —— 字符串入口（对齐 NumberOf 的字符串分支）。</summary>
    public static double NumberOfString(string? s)
    {
        if (s is null) return double.NaN;
        if (s.Trim().Length == 0) return 0;
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? p : double.NaN;
    }

    /// <summary>TS: Math.round(v) —— half-up（.NET Math.Round 为 banker's rounding，不能直接用）。</summary>
    public static double JsMathRound(double v) => Math.Floor(v + 0.5);

    /// <summary>
    /// TS: Date.parse(s) 语义的宽松子集——尝试解析 ISO 8601 / 常见日期时间字符串；失败返回 NaN。
    /// 本域只消费 PowerShell ConvertTo-Json 输出的 ISO 形式（如 2024-01-02T03:04:05.123），
    /// 该形态 TS 的 Date.parse 稳定解析。
    /// </summary>
    public static double DateParseMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
        {
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }
        return double.NaN;
    }

    /// <summary>TS: new Date(ms).toISOString().slice(0, 10) —— 毫秒时间戳取 UTC 日期串（yyyy-MM-dd）。</summary>
    public static string IsoDate(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>TS: Date.now()（毫秒）。</summary>
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// TS: firstOrNull / firstOrNull —— 单值期望查询归一化：JSON 解析对「无结果」输出空数组（truthy）。
    /// 存在性检查必须取首元素，否则条件恒真。同时把 null 收敛为 null。
    /// </summary>
    public static JsonElement? FirstOrNull(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            using IEnumerator<JsonElement> it = e.EnumerateArray().GetEnumerator();
            return it.MoveNext() ? it.Current.Clone() : null;
        }
        if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return e;
    }

    /// <summary>TS: JSON 形态归一为数组（单对象 / null / 数组三种形状统一）。</summary>
    public static List<JsonElement> ToArray(JsonElement e)
    {
        var list = new List<JsonElement>();
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in e.EnumerateArray()) list.Add(item.Clone());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            default:
                list.Add(e.Clone());
                break;
        }
        return list;
    }

    /// <summary>TS: typeof x === 'string' &amp;&amp; x.length &gt; 0 —— 取非空字符串属性。</summary>
    public static string? NonEmptyStringProp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        string s = v.GetString() ?? "";
        return s.Length > 0 ? s : null;
    }

    /// <summary>TS: v === false（显式布尔 false 判定；缺失/非布尔 → false）。</summary>
    public static bool IsExplicitFalse(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        return obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.False;
    }

    /// <summary>TS: v === true（显式布尔 true 判定；缺失/非布尔 → false）。</summary>
    public static bool IsExplicitTrue(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        return obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>TS: String(v) —— 任意 JSON 值转字符串（null → ''）。</summary>
    public static string StringOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.Undefined => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        _ => e.GetRawText(),
    };

    /// <summary>JS 正则的 \w = [A-Za-z0-9_]（.NET 默认 \w 含 Unicode 字母，须显式收窄）。</summary>
    public static Regex JsWordClass(string body) => new(body, RegexOptions.CultureInvariant);

    /// <summary>
    /// TS: /^[A-Za-z]$/.test(x) —— 单字母盘符判定。
    /// </summary>
    public static bool IsSingleLetter(string s) => s.Length == 1 && s[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
