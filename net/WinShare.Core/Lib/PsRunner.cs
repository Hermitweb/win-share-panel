// 移植自 electron/lib/powershell.ts 的 runPowerShell / runPowerShellVoid 语义。
// 执行通道 = ProcessPowerShell（out-of-process Windows PowerShell 5.1，逐字对齐 TS execOnce）：
// 本机实测进程内 SMA 7.5 无法加载 SmbShare/WebAdministration 等 5.1 脚本模块，故恢复 TS 的 spawn 路径。
// T1 的进程内池（PoolSingleton）保留但不再是默认通道。
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Ps;

namespace WinShare.Core.Lib;

/// <summary>
/// PowerShell 执行通道抽象（Lib 自定，供服务域注入假实现——同 TS vi.mock('../lib/powershell') 手法）。
/// 生产实现为 <see cref="PsRunner"/>（经 PoolSingleton 常驻池 + PsArgs 编码闸门）。
/// </summary>
public interface IPsInvoker
{
    /// <summary>
    /// TS: runPowerShell&lt;T&gt;(command, { timeout, retries })——JSON 模式。
    /// 空 stdout 收敛为空数组（TS parseJson 语义）；解析失败/执行失败抛 <see cref="PsInvocationException"/>
    /// （消息为 formatPsError 归一化后的"人话"文案）。
    /// </summary>
    Task<JsonElement> RunJsonAsync(string command, int timeoutMs, int retries);

    /// <summary>TS: runPowerShellVoid(command, { timeout, retries })——VOID 模式（Set-/New-/Remove- 用）。</summary>
    Task RunVoidAsync(string command, int timeoutMs, int retries);
}

/// <summary>
/// TS: runPowerShell/runPowerShellVoid 最终 throw 的 Error（formatPsError 归一化消息）——
/// C# 侧以独立异常类型承载，避免与 PsCommandFailedException 等池层原始错误混淆。
/// </summary>
public sealed class PsInvocationException : Exception
{
    public PsInvocationException(string message) : base(message) { }
}

/// <summary>
/// 生产实现：PoolSingleton 常驻池（JSON/VOID 模式）+ 重试 + 错误归一化。
/// 与 TS 的差异：TS 在 WINSHARE_PSPOOL=0 时回退 nativePowerShellExe 拉进程（execOnce），
/// T1 的 C# Ps 层未移植进程回退面，故本类无条件走池（PoolSingleton.GetPool 不受该开关影响）。
/// </summary>
public sealed class PsRunner : IPsInvoker
{
    /// <summary>TS: DEFAULT_TIMEOUT = 15000。</summary>
    public const int DefaultTimeoutMs = 15000;
    /// <summary>TS: DEFAULT_RETRIES = 2（总尝试次数 = retries + 1）。</summary>
    public const int DefaultRetries = 2;

    /// <summary>生产侧默认实例。</summary>
    public static readonly PsRunner Default = new();

    // TS: isRetryable — /timed out|ETIMEDOUT|ECONNRESET|temporarily|重试/i
    private static readonly Regex Retryable = new(@"timed out|ETIMEDOUT|ECONNRESET|temporarily|重试",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // TS: formatPsError 超时判定 — /timed out|ETIMEDOUT/i
    private static readonly Regex TimeoutPattern = new(@"timed out|ETIMEDOUT",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // TS: spawn 阶段 ENOENT（0x80070003）
    private static readonly Regex SpawnPattern = new(@"ENOENT|0x80070003|文件找不到|系统找不到",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // TS: /access is denied|拒绝访问/i
    private static readonly Regex AccessDenied = new(@"access is denied|拒绝访问",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClixmlHeader = new(@"^#<\s*CLIXML\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ClixmlErrorTags = new(@"<S S=""(Error|Warning)""[^>]*>([\s\S]*?)</S>", RegexOptions.Compiled);
    private static readonly Regex XmlHexEscape = new(@"_x([0-9A-Fa-f]{4})_", RegexOptions.Compiled);
    private static readonly Regex PreparingModule = new(@"准备.*模块|preparing.*module", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>TS: parseJson 空 stdout → []。</summary>
    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

    /// <summary>TS: runPowerShell — 带超时与自动重试（可重试错误 sleep 300*(attempt+1)）。</summary>
    public async Task<JsonElement> RunJsonAsync(string command, int timeoutMs = DefaultTimeoutMs, int retries = DefaultRetries)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                string stdout = await ProcessPowerShell.ExecuteAsync(command, withJson: true, timeoutMs);
                return ParseJson(stdout);
            }
            catch (Exception err)
            {
                lastError = err;
                if (!Retryable.IsMatch(err.Message) || attempt == retries) break;
                await Task.Delay(300 * (attempt + 1));
            }
        }
        throw new PsInvocationException(FormatPsError(lastError));
    }

    /// <summary>TS: runPowerShellVoid — 同上，无返回值。</summary>
    public async Task RunVoidAsync(string command, int timeoutMs = DefaultTimeoutMs, int retries = DefaultRetries)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                await ProcessPowerShell.ExecuteAsync(command, withJson: false, timeoutMs);
                return;
            }
            catch (Exception err)
            {
                lastError = err;
                if (!Retryable.IsMatch(err.Message) || attempt == retries) break;
                await Task.Delay(300 * (attempt + 1));
            }
        }
        throw new PsInvocationException(FormatPsError(lastError));
    }

    // TS: parseJson — 空/whitespace → []；JSON.parse 失败 → 错误（上层 formatPsError 归一化）
    internal static JsonElement ParseJson(string stdout)
    {
        string trimmed = (stdout ?? "").Trim();
        if (trimmed.Length == 0) return EmptyArray;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.Clone();
        }
        catch
        {
            string preview = trimmed.Length > 200 ? trimmed[..200] : trimmed;
            throw new Exception($"PowerShell 输出解析失败：{preview}");
        }
    }

    // TS: formatPsError — 每种失败归到一句人话（"命令执行失败"对用户是正常状态，收敛为可展示文案）
    internal static string FormatPsError(Exception? err)
    {
        if (err is null) return "未知错误";
        // 池层错误对象携带 stderr 字段（等价 TS (err as Error & { stderr?: string }).stderr）
        string rawStderr = err is PsCommandFailedException f ? f.Stderr : "";
        string rawMsg = err.Message ?? "";
        string combined = rawStderr.Length > 0 ? rawStderr : rawMsg;
        if (TimeoutPattern.IsMatch(rawMsg))
        {
            return "命令执行超时，请检查系统响应或稍后重试";
        }
        if (SpawnPattern.IsMatch(rawMsg))
        {
            return "PowerShell 无法启动（文件找不到或被拦截）：详见「系统环境」卡的诊断建议";
        }
        string readable = StripClixml(combined);
        if (AccessDenied.IsMatch(readable))
        {
            return "权限不足，请以管理员身份运行";
        }
        string tail = readable.Length > 300 ? readable[..300] : readable;
        return $"命令执行失败：{tail}";
    }

    // TS: stripClixml — 剥离 CLIXML 序列化格式，提取可读错误文本
    private static string StripClixml(string text)
    {
        if (!text.Contains("CLIXML")) return text;
        var matches = ClixmlErrorTags.Matches(text);
        if (matches.Count > 0)
        {
            // 优先提取 Error/Warning 流文本
            return string.Join(" ",
                matches.Cast<Match>()
                    .Select(m => DecodeHexEscapes(m.Groups[2].Value).Trim())
            ).Trim();
        }
        // 无 Error/Warning 标签：剥离所有 CLIXML XML 标签，提取剩余纯文本
        string stripped = XmlTag.Replace(ClixmlHeader.Replace(text, ""), " ");
        stripped = DecodeHexEscapes(stripped);
        stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
        if (stripped.Length == 0 || PreparingModule.IsMatch(stripped))
        {
            return "PowerShell 命令执行失败（无详细错误信息）";
        }
        return stripped;
    }

    /// <summary>TS: s.replace(/_x([0-9A-Fa-f]{4})_/g, hex → String.fromCharCode(parseInt(hex,16)))。</summary>
    private static string DecodeHexEscapes(string s) =>
        XmlHexEscape.Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
