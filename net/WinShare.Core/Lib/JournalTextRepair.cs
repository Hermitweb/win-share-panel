using System.Text;

namespace WinShare.Core.Lib;

/// <summary>
/// 历史乱码修复（v2.3）：旧版 Electron 的 PowerShell 链路把 UTF-8 字节按系统代码页（中文 Windows=GBK/936）
/// 解码，导致 journal 台账的 name/detail 存成乱码（如 "鍏变韩宸插垹闄わ紙" 实为 "共享已删除（"）。
/// 逆转：乱码 --GBK(936)编码--> 原始字节 --严格 UTF-8 解码--> 原文。
/// 逐段贪心：整段不可逆时切最长可解段，单字符不可解则保留——最大化救回中文。
/// 幂等：修复后的文本无可解段，再跑不变。当前代码库已无 GBK 解码路径，本工具只修历史数据。
/// 下沉到 Core：JournalService（WPF/Electron 共用台账）与 WPF AppSettingsStore 都经此修复。
/// </summary>
public static class JournalTextRepair
{
    static readonly Encoding Gbk;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static JournalTextRepair()
    {
        // .NET Core 默认不含非 Unicode 代码页：自足注册（未注册时 GetEncoding(936) 抛异常）
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 已注册 */ }
        Gbk = Encoding.GetEncoding(936);
    }

    public static string? Repair(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // 短路：不含中文基本区字符（纯 ASCII 路径/英文）直接返回
        bool hasCjk = false;
        foreach (char c in s)
        {
            if (IsCjk(c)) { hasCjk = true; break; }
        }
        if (!hasCjk) return s;

        var sb = new StringBuilder(s.Length);
        int i = 0, n = s.Length;
        while (i < n)
        {
            string? best = null;
            int bestEnd = -1;
            // 从最长子段开始试，命中即停（最长优先）
            for (int j = n; j > i; j--)
            {
                if (best is not null && j - i <= bestEnd - i) break;
                string? t = TryDecode(s.Substring(i, j - i));
                if (t is not null) { best = t; bestEnd = j; break; }
            }
            if (best is not null) { sb.Append(best); i = bestEnd; }
            else { sb.Append(s[i]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>乱码段 → 原文；不可逆（非法 UTF-8/无中文/无变化）返回 null。</summary>
    static string? TryDecode(string seg)
    {
        try
        {
            // 逐字符编码再拼接：避免跨字符替换符干扰，复原率最高
            var buf = new List<byte>(seg.Length * 3);
            foreach (char c in seg)
            {
                buf.AddRange(Gbk.GetBytes(new[] { c }));
            }
            string t = StrictUtf8.GetString(buf.ToArray());
            if (t.Length == 0 || t == seg) return null;
            foreach (char c in t)
            {
                if (IsCjk(c)) return t;
            }
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}
