using System;
using System.Collections.Generic;
using WinShare.Core.Contracts;

namespace WinShare.App.Common;

/// <summary>
/// 错误友好化（表驱动，纯静态可单测）：
/// 把服务层/PS 层抛上来的异常翻译成「人话 + 下一步建议」，替代 catch { Error = e.Message } 的
/// stderr 原文直出。结构化信号（AppException.Code/Category）优先，文本模式兜底；
/// 全部未命中时截取关键行并提示「详情见应用日志」。Core 层错误归一（PsRunner.FormatPsError）不动。
/// </summary>
public static class ErrorText
{
    /// <summary>单条文本模式规则（正则大小写不敏感；命中即返回人话）。</summary>
    public sealed record Rule(string Pattern, string Friendly);

    /// <summary>
    /// 规则表（顺序即优先级）：
    /// PS stderr 常见错误 → 人话 + 建议；「服务未注册/未接入/组合根」等内部降级术语 → 统一口径。
    /// </summary>
    public static readonly IReadOnlyList<Rule> Rules = new List<Rule>
    {
        new(@"服务未注册|未接入|未接线|组合根|契约服务", "功能所需的后台服务未就绪：请重启应用后重试；若持续出现，请到「应用设置 → 应用日志」查看详情"),
        new(@"需要管理员|权限不足|access is denied|拒绝访问|拒绝访问。", "需要管理员权限：请点击顶栏提示条或「应用设置 → 关于 → 以管理员身份重启」后重试"),
        new(@"超时|timeout|timed out", "操作超时：本机 PowerShell 响应较慢，请稍后重试"),
        new(@"已在?存在|already exists|alrea?dy\\s+exist", "该名称已被占用：请换一个共享名/规则名后重试"),
        new(@"找不到.*路径|路径.*找不到|找不到文件|path not found|cannot find the (path|file)|does not exist|指定的路径", "路径或对象不存在：请确认文件夹/共享是否还在（可能已被移动、重命名或删除）"),
        new(@"端口.*(占用|冲突|in use)|in use|already listening|绑定失败", "端口被其他程序占用：请换一个端口，或到「应用设置 → 偏好与运维」调整被动端口段"),
        new(@"being used by another|正由另一进程|正被另一进程", "文件正被其他程序占用：请关闭占用它的程序（如正在编辑/上传中）后重试"),
        new(@"未安装|not installed|not present|角色服务", "对应功能尚未安装：请先到设置页对应协议选项卡点击「安装」"),
        new(@"服务.*(未运行|已停止)|is not running|stopped", "后台服务未运行：请到设置页对应协议选项卡点「启动」"),
        new(@"IIS|appcmd|applicationHost", "IIS 配置异常：请确认 IIS 已安装完整、配置节未被锁定，再重试"),
        new(@"网络|network|连接失败|无法连接|无法访问", "网络连接异常：请检查本机网络与目标主机可达性后重试"),
        new(@"用户不存在|找不到用户|user (not found|does not exist)|cannot find.*(user|account)|the user name could not be found", "账号不存在：请确认用户名拼写，或到「用户权限」页查看现有账号"),
        new(@"参数|无效的|invalid", "输入参数不合法：请检查填写内容（名称/路径/端口/账号名）"),
    };

    /// <summary>
    /// 友好化主入口：action 为「正在做的动作名」（如 创建共享/保存配置），输出「{action}失败：{人话}」；
    /// action 传 null 时只输出人话本身。异常为 null 时返回通用失败文案。
    /// </summary>
    public static string Describe(Exception? e, string? action = null)
    {
        string friendly = Friendly(e);
        return string.IsNullOrEmpty(action) ? friendly : $"{action}失败：{friendly}";
    }

    /// <summary>短版（批量失败逐条列出用）：只取人话/关键行，不带 action 前缀、不带日志指引，控制在 60 字内。</summary>
    public static string Brief(Exception? e)
    {
        if (e is null) return "原因未知";
        if (e is AppException app)
        {
            if (app.Category == ErrorCategory.Permission) return "需要管理员权限";
            string viaCode = MatchRules(app.Message);
            if (viaCode.Length > 0) return FirstSentence(viaCode);
            return Trunc(app.Message, 60);
        }
        string hit = MatchRules(e.Message ?? "");
        if (hit.Length > 0) return FirstSentence(hit);
        return Trunc(ExtractKeyLine(e.Message ?? "", withLogHint: false), 60);
    }

    /// <summary>取「人话 + 建议」的第一句（批量摘要里不需要整段建议）。</summary>
    static string FirstSentence(string s)
    {
        int cut = s.IndexOf('：');
        return cut > 0 ? s[..cut] : s;
    }

    static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "原因未知" : s.Length <= max ? s : s[..max] + "…";

    static string Friendly(Exception? e)
    {
        if (e is null) return "操作未完成，原因未知（详情见应用日志）";

        // ① 结构化信号优先：AppException 分类直译
        if (e is AppException app)
        {
            if (app.Category == ErrorCategory.Permission) return Rules[1].Friendly;
            string viaCode = MatchRules(app.Message);
            if (viaCode.Length > 0) return viaCode;
            return app.Message.Length <= 140 ? app.Message : app.Message[..140] + "…（详情见应用日志）";
        }

        // ② 文本模式兜底（PS stderr / Io 异常 / COM 异常等）
        string msg = e.Message ?? "";
        string hit = MatchRules(msg);
        if (hit.Length > 0) return hit;

        // ③ 兜底：剥「命令执行失败：」前缀 → 取首个有意义行 → 截 140 字 + 日志指引
        return ExtractKeyLine(msg);
    }

    static string MatchRules(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "";
        foreach (var r in Rules)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(msg, r.Pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return r.Friendly;
        }
        return "";
    }

    /// <summary>
    /// 兜底关键行提取：PS 的 CLIXML stderr 常是「命令执行失败：New-SmbShare : ... 段落」；
    /// 剥已知前缀后取第一行非空、非「+ CategoryInfo/FullyQualifiedErrorId」调试尾巴的行。
    /// 日志指引只在原文确实是 PS 调试形态/超长时追加——服务层已人话化的短文案不再加噪音。
    /// </summary>
    public static string ExtractKeyLine(string msg, bool withLogHint = true)
    {
        string Fallback() => withLogHint ? "操作未完成（详情见应用日志）" : "操作未完成";
        if (string.IsNullOrWhiteSpace(msg)) return Fallback();
        string s = msg.Replace("\r", "\n").Trim();
        bool technical = msg.Contains("CategoryInfo", StringComparison.Ordinal)
            || msg.Contains("At line", StringComparison.Ordinal)
            || msg.Contains("FullyQualifiedErrorId", StringComparison.Ordinal)
            || msg.Length > 120;
        foreach (var prefix in new[] { "命令执行失败：", "命令执行失败:", "操作失败：" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..].Trim();
        }
        foreach (var line in s.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("+ CategoryInfo") || t.StartsWith("+ FullyQualifiedErrorId")) continue;
            string cut = t.Length > 140 ? t[..140] + "…" : t;
            return withLogHint && technical ? cut + "（详情见应用日志）" : cut;
        }
        return Fallback();
    }
}
