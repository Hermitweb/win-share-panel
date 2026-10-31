// 移植自 electron/services/update.ts（应用内「检查更新」：只做检查与提示，不做自动下载安装）。
// 失败路径比成功路径更需要做好：每种失败都归到一句人话原因 + 一个可执行动作。
// C# 侧 HTTP 走 HttpClient；TS 的 fetchImpl 注入点对应为 Fetcher 委托（测试用）。
using System.Net.Http;
using System.Text.Json;
using WinShare.Core.Contracts;

namespace WinShare.Core.Services.Misc;

/// <summary>
/// TS: update.ts MinimalResponse（收窄的最小响应接口，避免把完整 HTTP 类型拖进调用方）。
/// Body 为 null 表示"JSON 不可得"（响应体不是合法 JSON 或为空）——对应 TS 里 res.json() 抛错的分支。
/// </summary>
public sealed class MinimalUpdateResponse
{
    public bool Ok { get; init; }
    public int Status { get; init; }
    public JsonElement? Body { get; init; }
}

/// <summary>TS: FetchLike (url, init?: { signal }) — signal 以 CancellationToken 承载。</summary>
public delegate Task<MinimalUpdateResponse> UpdateFetchLike(string url, CancellationToken cancellationToken);

/// <summary>
/// IUpdateService 实现（HTTP 走 HttpClient；测试经 <see cref="Fetcher"/> 注入假 fetch）。
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>TS: DEFAULT_TIMEOUT_MS = 8000。</summary>
    public const int DefaultTimeoutMs = 8000;

    private const string RepoConst = "Hermitweb/win-share-panel";
    private const string ReleasesPageConst = $"https://github.com/{RepoConst}/releases/latest";
    private const string ApiReleasesConst = $"https://api.github.com/repos/{RepoConst}/releases?per_page=30";

    /// <summary>TS: UPDATE_REPO（更新源：与 git remote / release workflow 指向的仓库一致）。</summary>
    public string UpdateRepo => RepoConst;

    /// <summary>TS: RELEASES_PAGE（给用户点的下载页，无论检查成功与否都可打开）。</summary>
    public string ReleasesPage => ReleasesPageConst;

    /// <summary>WPF 更新源 API：releases 列表（非 /releases/latest——仓库现并存两条版本线：
    /// Electron 走 v* tag、WPF 走 wpf-v* tag，必须按前缀过滤，否则互相误报升级）。</summary>
    public string ApiReleases => ApiReleasesConst;

    /// <summary>WPF Release 的 tag 前缀（wpf-v2.0.0 → 2.0.0）。</summary>
    public static System.Text.RegularExpressions.Regex WpfTagPattern { get; } =
        new("^wpf-v\\d",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);

    /// <summary>测试注入点：自定义 fetch 实现；null 时走默认 HttpClient fetch。</summary>
    public UpdateFetchLike? Fetcher { get; set; }

    private readonly HttpClient _http;
    private readonly HttpClient? _ownedHttp;

    public UpdateService(HttpClient? http = null)
    {
        _ownedHttp = http;
        _http = http ?? new HttpClient();
    }

    public int CompareVersions(string a, string b)
    {
        (int[] Nums, string Pre) pa = ParseVersion(a);
        (int[] Nums, string Pre) pb = ParseVersion(b);
        int len = Math.Max(pa.Nums.Length, pb.Nums.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < pa.Nums.Length ? pa.Nums[i] : 0;
            int y = i < pb.Nums.Length ? pb.Nums[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        if (pa.Pre == pb.Pre) return 0;
        if (pa.Pre.Length == 0) return 1;
        if (pb.Pre.Length == 0) return -1;
        return string.CompareOrdinal(pa.Pre, pb.Pre) < 0 ? -1 : 1;
    }

    /// <summary>
    /// TS: checkForUpdate(currentVersion, fetchImpl?, timeoutMs=8000)。
    /// **不抛错**：一切失败都收敛成 unavailable + reason，因为「检查更新失败」对用户是正常状态。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, int timeoutMs = DefaultTimeoutMs)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(timeoutMs);
        try
        {
            MinimalUpdateResponse res;
            try
            {
                res = Fetcher is { } fetcher
                    ? await fetcher(ApiReleases, cts.Token)
                    : await DefaultFetch(cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                // TS: controller.signal.aborted → 报超时而不是网络错误
                return Unavailable(currentVersion, $"检查更新超时（超过 {timeoutMs} 毫秒未响应）");
            }
            catch (Exception e)
            {
                string msg = e.Message;
                return Unavailable(
                    currentVersion,
                    $"无法连接到更新源（{UpdateRepo}）：{msg}。若处于受限网络，可配置代理后重试，或直接打开下载页手动查看。");
            }

            if (res.Status == 404)
            {
                return Unavailable(currentVersion, "更新源上没有已发布的 Release（仓库不存在或尚未发版）");
            }
            if (!res.Ok)
            {
                return Unavailable(currentVersion, $"更新源返回 HTTP {res.Status}");
            }

            // TS: try { body = await res.json() } catch → '响应不是合法 JSON'；
            // C# 急切承载：Body == null 即 JSON 不可得（含空响应体与非 JSON 响应体）。
            if (res.Body is not { } body || body.ValueKind is JsonValueKind.Undefined)
            {
                return Unavailable(currentVersion, "更新源响应不是合法 JSON");
            }

            // 双版本线（2026-10-31 起）：Electron 走 v* tag、WPF 走 wpf-v* tag，同一 releases
            // 列表按前缀各取最新——/releases/latest 不分前缀，会互相误报升级，故弃用。
            // GitHub 列表按发布时间新→旧排序：取第一个匹配 wpf-v* 的条目即 WPF 最新 Release。
            if (body.ValueKind != JsonValueKind.Array)
            {
                return Unavailable(currentVersion, "更新源响应不是 Release 列表");
            }
            JsonElement obj = default;
            foreach (var item in body.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("tag_name", out var tn)
                    && tn.ValueKind == JsonValueKind.String
                    && WpfTagPattern.IsMatch(tn.GetString()!))
                {
                    obj = item;
                    break;
                }
            }
            if (obj.ValueKind != JsonValueKind.Object)
            {
                return Unavailable(currentVersion, "更新源上还没有 WPF 版 Release（wpf-v* tag）");
            }

            string tag = obj.TryGetProperty("tag_name", out var tagEl)
                && tagEl.ValueKind == JsonValueKind.String
                ? tagEl.GetString()!.Trim()
                : "";

            // wpf-v2.0.0 → 2.0.0
            string latest = System.Text.RegularExpressions.Regex.Replace(tag, "^wpf-v", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new UpdateCheckResult
            {
                current = currentVersion,
                latest = latest,
                hasUpdate = CompareVersions(latest, currentVersion) > 0,
                releaseUrl = obj.TryGetProperty("html_url", out var hu)
                    && hu.ValueKind == JsonValueKind.String
                    && hu.GetString() is { Length: > 0 }
                    ? hu.GetString()!
                    : ReleasesPage,
                publishedAt = obj.TryGetProperty("published_at", out var pa)
                    && pa.ValueKind == JsonValueKind.String
                    ? pa.GetString()
                    : null,
                unavailable = false,
                reason = null,
            };
        }
        finally
        {
            // TS: finally clearTimeout(timer)
        }
    }

    private static UpdateCheckResult Unavailable(string current, string reason) => new()
    {
        current = current,
        latest = "",
        hasUpdate = false,
        releaseUrl = ReleasesPageConst,
        publishedAt = null,
        unavailable = true,
        reason = reason,
    };

    /// <summary>TS: defaultFetch — User-Agent WinSharePanel + GitHub API Accept 头。</summary>
    private async Task<MinimalUpdateResponse> DefaultFetch(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, ApiReleases);
        req.Headers.Add("User-Agent", "WinSharePanel");
        req.Headers.Add("Accept", "application/vnd.github+json");
        HttpResponseMessage resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement? body = null;
        if (text.Trim().Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                body = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                body = null;
            }
        }
        int status = (int)resp.StatusCode;
        return new MinimalUpdateResponse
        {
            Ok = status >= 200 && status < 300,
            Status = status,
            Body = body,
        };
    }

    // TS: parseVersion — 去掉 v 前缀并拆成 [数值段, 预发布标识]，供语义化比较
    private static (int[] Nums, string Pre) ParseVersion(string v)
    {
        string s = v.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        string[] parts = s.Split('-');
        string core = parts[0];
        string pre = string.Join("-", parts.Skip(1));
        var nums = core.Split('.').Select(ParseIntJs).ToArray();
        return (nums, pre);
    }

    /// <summary>JS Number.parseInt(x, 10) 等价：取前导数字部分，无数字 → 0。</summary>
    private static int ParseIntJs(string x)
    {
        var m = System.Text.RegularExpressions.Regex.Match(x, @"^[+-]?\d*");
        string lead = m.Success ? m.Value : "";
        if (lead.Length == 0 || lead is "+" or "-") return 0;
        return int.TryParse(lead, out int n) ? n : 0;
    }
}
