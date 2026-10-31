namespace WinShare.App.Views.Settings;

/// <summary>
/// 切页刷新节流。
///
/// 背景：导航不做页面缓存，每次切页都会重建页面，页面的 Loaded 又会拉一整轮数据
/// （4 个协议的 PowerShell 查询，本机实测单轮 16-21s）。快速来回点侧栏时这些轮次会堆叠，
/// 把 3 个 worker 的 PowerShell 池占满 —— 表现就是「点击界面太卡」。
///
/// 页面重建是常态，但「重建就必须重拉」不成立：同一页面 N 秒内的数据差异可以忽略。
/// 节流键按页面固定，静态持有，因此跨页面重建也生效。
/// </summary>
public static class PageRefreshThrottle
{
    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> Last = new();

    /// <summary>是否应该刷新（同一 key 在 seconds 秒内返回 false）。</summary>
    public static bool ShouldRefresh(string key, int seconds = 10)
    {
        lock (Gate)
        {
            if (Last.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < seconds)
            {
                return false;
            }
            Last[key] = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>测试钩子：清空节流状态。</summary>
    public static void ResetForTesting()
    {
        lock (Gate) Last.Clear();
    }
}
