// JSON 序列化兼容层：对齐 TS 侧 JSON.stringify 的字节级语义。
// 背景（实测缺陷）：System.Text.Json 默认把非 ASCII 转义成 \uXXXX，而 JSON.stringify
// 原样输出 UTF-8 中文——导致 C# 写出的 disabled.json/presets.json/appstate.json/配置导出
// 与 Electron 版数据文件不互通（内容不符、人工不可读）。
// 统一走 JavaScriptEncoder.UnsafeRelaxedJsonEscaping（不转义中文/emoji；仅用于非 HTML 数据文件）。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShare.Core.Lib;

public static class JsonCompat
{
    /// <summary>对齐 JSON.stringify(v, null, 2)：2 空格缩进 + 不转义非 ASCII。</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>对齐 JSON.stringify(v)：紧凑 + 不转义非 ASCII（JSONL/审计行用）。</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>枚举转小驼峰字符串（theme: light/dark 等）+ 2 空格缩进 + 不转义非 ASCII。</summary>
    public static readonly JsonSerializerOptions IndentedWithEnums = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>读侧容错（字段名大小写不敏感）。</summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Stringify(object? value) => JsonSerializer.Serialize(value, Compact);

    public static string StringifyIndented(object? value) => JsonSerializer.Serialize(value, Indented);
}
