// PS 回读 JSON 的 JS 语义取值助手。
// TS 侧直接写 raw.X / String(v) / Number(v) / !!v；C# 的 JsonElement 在非对象上取属性会抛异常，
// 且 !!v 有 JS 特有真值表（false/0/''/null 为假）。三个协议服务共用本类，
// 保证「字段缺失 / 为 null / 类型不符」的处理与 TS 逐字一致。
using System.Globalization;
using System.Text.Json;

namespace WinShare.Core.Services.ProtocolServices;

internal static class ConfigJson
{
    /// <summary>
    /// 取可读属性的对象：对象自身；PowerShell 单元素数组取首元素；其余视为「无属性」。
    /// JsonValueKind.Undefined 上调用 TryGetProperty 会抛异常，故统一收敛到 Undefined 元素。
    /// </summary>
    public static JsonElement Obj(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object) return raw;
        if (raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0)
        {
            JsonElement first = raw[0];
            if (first.ValueKind == JsonValueKind.Object) return first;
        }
        return default; // 等价 TS 的 raw 为 null/[] 时所有 raw.X === undefined
    }

    /// <summary>TS: raw[name]（不存在 → null）。</summary>
    public static JsonElement? Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty(name, out JsonElement v) ? v : null;
    }

    /// <summary>TS: v === undefined || v === null。</summary>
    public static bool IsAbsentOrNull(JsonElement obj, string name) =>
        Prop(obj, name) is not JsonElement p || p.ValueKind == JsonValueKind.Null;

    /// <summary>TS: !!v（JS 真值表：false / 0 / '' / null / undefined 为假，其余为真）。</summary>
    public static bool Truthy(JsonElement obj, string name)
    {
        if (Prop(obj, name) is not JsonElement p) return false;
        return p.ValueKind switch
        {
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => p.TryGetDouble(out double d) && d != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(p.GetString()),
            _ => true, // 对象 / 数组恒真
        };
    }

    /// <summary>TS: v !== undefined ? !!v : 默认值。</summary>
    public static bool Bool(JsonElement obj, string name, bool fallback) =>
        Prop(obj, name) is null ? fallback : Truthy(obj, name);

    /// <summary>TS: typeof v === 'string' ? v : ''（仅接受真正的字符串）。</summary>
    public static string? Str(JsonElement obj, string name) =>
        Prop(obj, name) is JsonElement p && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>TS: String(v) —— 仅覆盖 JSON 基本类型（对象 / 数组无朴素文本对应，返回 null）。</summary>
    public static string? JsString(JsonElement obj, string name)
    {
        if (Prop(obj, name) is not JsonElement p) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => null,
        };
    }

    /// <summary>TS: Number(v) —— NaN 以 null 表示（C# 整型字段无法承载 NaN）。</summary>
    public static double? Number(JsonElement obj, string name)
    {
        if (Prop(obj, name) is not JsonElement p) return null;
        switch (p.ValueKind)
        {
            case JsonValueKind.Number:
                return p.TryGetDouble(out double d) ? d : null;
            case JsonValueKind.String:
                return double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd)
                    ? sd
                    : null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Null: return 0;
            default: return null;
        }
    }

    /// <summary>TS: v !== undefined ? Number(v) : 默认值（读取到 0 仍保留 0）。</summary>
    public static int Int(JsonElement obj, string name, int fallback) =>
        Prop(obj, name) is null ? fallback
        : Number(obj, name) is double d ? (int)d : fallback;

    /// <summary>TS: Number(v) || 默认值（0 回落默认值）。</summary>
    public static int IntOr(JsonElement obj, string name, int fallback) =>
        Number(obj, name) is double d && d != 0 ? (int)d : fallback;

    /// <summary>TS: Number(v) || 默认值（long 字段）。</summary>
    public static long LongOr(JsonElement obj, string name, long fallback) =>
        Number(obj, name) is double d && d != 0 ? (long)d : fallback;
}

/// <summary>按 TS 值解析枚举名（非法值回落默认），用于 IIS 配置节回读字符串 → 契约枚举。</summary>
internal static class ConfigEnum
{
    public static T Parse<T>(string? raw, T fallback) where T : struct, Enum =>
        raw is not null && Enum.TryParse(raw, ignoreCase: true, out T parsed) ? parsed : fallback;
}
