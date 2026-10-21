// 测试 scratch 目录助手：testhost 在 DSH 沙箱（workspace-write）下运行，
// %TEMP% 不可写（UnauthorizedAccessException），故所有测试临时目录统一落在
// workspace 内的 Tests/scratch/ 下（bin/Debug/net10.0 上溯 3 级 = 测试工程根）。
namespace WinShare.Core.Tests.Misc;

internal static class TestTmp
{
    private static readonly string Root =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scratch"));

    /// <summary>新建独立 scratch 子目录（已创建）。</summary>
    public static string New(string prefix)
    {
        string dir = System.IO.Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("n")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
}
