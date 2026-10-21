// 移植自 electron/services/update.test.ts（TS: update.ts → C#: UpdateService）。
// TS 的 vi.useFakeTimers 超时用例 → C# 用 50ms 真实超时 + 监听取消的假 fetch 等价覆盖。
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Services.Misc;

namespace WinShare.Core.Tests.Misc;

public class UpdateTests
{
    private static UpdateService Svc() => new();

    // TS: 造一个最小响应
    private static MinimalUpdateResponse Res(int status, object? body)
    {
        JsonElement? bodyEl = body is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.Clone();
        return new MinimalUpdateResponse
        {
            Ok = status >= 200 && status < 300,
            Status = status,
            Body = bodyEl,
        };
    }

    // ===== compareVersions（语义化，含预发布）=====

    // [Source: update.test.ts#compareVersions > 相同版本返回 0（含 v 前缀与段数不等）]
    [Fact]
    public void CompareVersions_Equal()
    {
        Assert.Equal(0, Svc().CompareVersions("1.1.0", "1.1.0"));
        Assert.Equal(0, Svc().CompareVersions("v1.1.0", "1.1.0"));
        Assert.Equal(0, Svc().CompareVersions("1.1", "1.1.0"));
    }

    // [Source: update.test.ts#compareVersions > 落后/超前]
    [Fact]
    public void CompareVersions_BehindAndAhead()
    {
        Assert.True(Svc().CompareVersions("1.0.0", "1.1.0") < 0);
        Assert.True(Svc().CompareVersions("1.1.0", "1.0.0") > 0);
        Assert.True(Svc().CompareVersions("1.0.0", "1.0.1") < 0);
        Assert.True(Svc().CompareVersions("2.0.0", "1.9.9") > 0);
        Assert.True(Svc().CompareVersions("1.10.0", "1.9.0") > 0);
    }

    // [Source: update.test.ts#compareVersions > 预发布低于同号正式版（semver 规则）]
    [Fact]
    public void CompareVersions_PrereleaseLowerThanRelease()
    {
        Assert.True(Svc().CompareVersions("1.2.0-beta.1", "1.2.0") < 0);
        Assert.True(Svc().CompareVersions("1.2.0", "1.2.0-beta.1") > 0);
        Assert.True(Svc().CompareVersions("1.2.0-beta.1", "1.2.0-beta.2") < 0);
    }

    // ===== checkForUpdate（不抛错：失败收敛为 unavailable + reason）=====

    // [Source: update.test.ts#checkForUpdate > 有新版本：latest > current → hasUpdate 为真]
    [Fact]
    public async Task Check_NewerVersionHasUpdate()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => Task.FromResult(Res(200, new
        {
            tag_name = "v1.2.0",
            html_url = "https://github.com/Hermitweb/win-share-panel/releases/tag/v1.2.0",
            published_at = "2026-10-01T00:00:00Z",
        }));
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.Equal("1.1.0", r.current);
        Assert.Equal("1.2.0", r.latest);
        Assert.True(r.hasUpdate);
        Assert.False(r.unavailable);
        Assert.Null(r.reason);
        Assert.Equal("2026-10-01T00:00:00Z", r.publishedAt);
        Assert.Contains("/releases/tag/v1.2.0", r.releaseUrl);
    }

    // [Source: update.test.ts#checkForUpdate > 已是最新：相同版本 → hasUpdate 为假且非 unavailable]
    [Fact]
    public async Task Check_UpToDate()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => Task.FromResult(Res(200, new { tag_name = "v1.1.0" }));
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.False(r.hasUpdate);
        Assert.False(r.unavailable);
        Assert.Equal("1.1.0", r.latest);
        Assert.Equal(svc.ReleasesPage, r.releaseUrl);
    }

    // [Source: update.test.ts#checkForUpdate > 比更新源还新（本地预发布）：不算有更新]
    [Fact]
    public async Task Check_LocalNewerNoUpdate()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => Task.FromResult(Res(200, new { tag_name = "v1.0.0" }));
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.False(r.hasUpdate);
    }

    // [Source: update.test.ts#checkForUpdate > 网络不可达：给出人话原因 + 仍提供下载页（本机实测的真实场景）]
    [Fact]
    public async Task Check_UnreachableGivesReadableReason()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => throw new HttpRequestException("fetch failed");
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.True(r.unavailable);
        Assert.False(r.hasUpdate);
        Assert.Contains("无法连接到更新源", r.reason);
        Assert.Contains("Hermitweb/win-share-panel", r.reason);
        Assert.Contains("下载页", r.reason);
        Assert.Equal(svc.ReleasesPage, r.releaseUrl);
    }

    // [Source: update.test.ts#checkForUpdate > 404：提示尚未发版，而不是 generic 失败]
    [Fact]
    public async Task Check_404NotReleasedYet()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => Task.FromResult(Res(404, new { }));
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.True(r.unavailable);
        Assert.Contains("没有已发布的 Release", r.reason);
    }

    // [Source: update.test.ts#checkForUpdate > 5xx：带状态码]
    [Fact]
    public async Task Check_5xxCarriesStatusCode()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) => Task.FromResult(Res(503, new { }));
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0");
        Assert.True(r.unavailable);
        Assert.Contains("503", r.reason);
    }

    // [Source: update.test.ts#checkForUpdate > 响应不是合法 JSON / 缺 tag_name：分别给不同原因]
    // 说明：TS 的 json() 抛错在 C# 以 Body == null 承载（急切解析，非法 JSON → null）。
    [Fact]
    public async Task Check_BadJsonOrMissingTag()
    {
        UpdateService badJson = Svc();
        badJson.Fetcher = (url, ct) => Task.FromResult(new MinimalUpdateResponse { Ok = true, Status = 200, Body = null });
        Assert.Contains("不是合法 JSON", (await badJson.CheckForUpdateAsync("1.1.0")).reason);

        UpdateService noTag = Svc();
        noTag.Fetcher = (url, ct) => Task.FromResult(Res(200, new { name = "no tag here" }));
        Assert.Contains("没有版本号", (await noTag.CheckForUpdateAsync("1.1.0")).reason);
    }

    // [Source: update.test.ts#checkForUpdate > 超时：abort 后报超时而不是网络错误]
    [Fact]
    public async Task Check_TimeoutReportsTimeout()
    {
        UpdateService svc = Svc();
        svc.Fetcher = (url, ct) =>
        {
            var tcs = new TaskCompletionSource<MinimalUpdateResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };
        UpdateCheckResult r = await svc.CheckForUpdateAsync("1.1.0", 50);
        Assert.True(r.unavailable);
        Assert.Contains("超时", r.reason);
    }
}
