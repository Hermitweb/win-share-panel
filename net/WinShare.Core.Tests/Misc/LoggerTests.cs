// 移植自 electron/lib/logger.test.ts（TS: logger.ts → C#: Logger）。
// TS 的 vi.spyOn(console,'log') 静默在 C# 无等价物，console 镜像不参与断言；
// 对象渲染断言按 C# 紧凑 JSON 语义对齐（TS 'name: a { n: 1 }' → C# 断言含 "name: a" 与 "\"n\":1"）。
using System.Text.RegularExpressions;
using WinShare.Core.Lib;
using WinShare.Core.Tests.Services.Protocols.Adapters;

namespace WinShare.Core.Tests.Misc;

// Logger 是进程级全局：本类的 2MB 轮转断言依赖「marker 在主文件尾部」的时序，
// 与协议适配器测试（每个方法都 Logger.Info）并行时会互相破坏 → 纳入同一独占集合串行运行。
[Collection(ProtocolAdaptersCollection.Name)]
public class LoggerTests
{
    private static readonly string TempDir = TestTmp.New("wslog-test");

    static LoggerTests()
    {
        // TS beforeAll：__setLogDirForTesting + __setFileLoggingForTesting(true)
        Logger.SetLogDirForTesting(TempDir);
        Logger.SetFileLoggingForTesting(true);
    }

    private static string LogFile => Path.Combine(TempDir, "app.log");

    private static string ReadFile() => File.Exists(LogFile) ? File.ReadAllText(LogFile) : "";

    // ===== logger 纯函数 =====

    // [Source: logger.test.ts#formatLine 含级别/时间戳/scope/文本，尾部空白剥离]
    [Fact]
    public void FormatLine_ContainsLevelScopeText_TrimsTrailing()
    {
        string line = Logger.FormatLine("warn", "2026-06-01T00:00:00.000Z", "smb", "  消息  ");
        Assert.Equal("[2026-06-01T00:00:00.000Z] [WARN] [smb]   消息", line);
    }

    // [Source: logger.test.ts#formatLine 超长截断至 8000]
    [Fact]
    public void FormatLine_TruncatesTo8000()
    {
        string line = Logger.FormatLine("info", "T", "s", new string('x', 10000));
        Assert.True(line.Length <= 8000 + "[T] [INFO] [s] ".Length, $"实际长度 {line.Length}");
    }

    // [Source: logger.test.ts#normalizeLevel 白名单与回退]
    [Fact]
    public void NormalizeLevel_WhitelistAndFallback()
    {
        Assert.Equal("error", Logger.NormalizeLevel("error"));
        Assert.Equal("debug", Logger.NormalizeLevel("debug"));
        Assert.Equal("info", Logger.NormalizeLevel("rm -rf"));
        Assert.Equal("info", Logger.NormalizeLevel(42));
        Assert.Equal("info", Logger.NormalizeLevel(null));
    }

    // ===== logger 文件写入 =====

    // [Source: logger.test.ts#writeLog 落盘并可 tail 读回]
    [Fact]
    public void WriteLog_PersistedAndTailReadable()
    {
        Logger.WriteLog("info", "test", "hello-一条中文日志");
        string content = ReadFile();
        Assert.Contains("[INFO] [test] hello-一条中文日志", content);
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^\[\d{4}-\d{2}-\d{2}T"), content);
    }

    // [Source: logger.test.ts#readLogTail 行数限制]
    [Fact]
    public void ReadLogTail_LimitsLineCount()
    {
        string sfx = Guid.NewGuid().ToString("n")[..8];
        for (int i = 0; i < 10; i++)
        {
            Logger.WriteLog("debug", "tail", $"line-{i}-{sfx}");
        }
        string tail = Logger.ReadLogTail(3);
        Assert.Equal(3, tail.Split('\n').Length);
        Assert.Contains($"line-9-{sfx}", tail);
        // TS: not.toContain('line-5\n')
        Assert.DoesNotContain($"line-5-{sfx}", tail);
    }

    // [Source: logger.test.ts#log.info 多参数与对象渲染（util.format）]
    [Fact]
    public void Info_MultiArgsAndObjectRendering()
    {
        Logger.Info("fmt", "name:", "a", new { n = 1 });
        string content = ReadFile();
        Assert.Contains("name: a", content);
        Assert.Contains("\"n\":1", content); // C# 紧凑 JSON（TS: '{ n: 1 }'）
    }

    // [Source: logger.test.ts#log.error 接收 Error 输出 stack]
    [Fact]
    public void Error_ExceptionRendersMessageAndStack()
    {
        Logger.Error("fmt", new Exception("boom-error-case"));
        string content = ReadFile();
        Assert.Contains("boom-error-case", content);
    }

    // [Source: logger.test.ts#超过 2MB 触发轮转（app.log.1 生成且最新内容在主文件）]
    [Fact]
    public void WriteLog_RotatesBeyond2Mb()
    {
        string marker = "after-rotate-marker-" + Guid.NewGuid().ToString("n")[..8];
        string big = new string('R', 8000);
        for (int i = 0; i < 280; i++)
        {
            Logger.WriteLog("info", "rotate", big); // ~2.24MB
        }
        Assert.True(File.Exists(Path.Combine(TempDir, "app.log.1")), "app.log.1 应已生成");
        Logger.WriteLog("info", "rotate", marker);
        Assert.Contains(marker, Logger.ReadLogTail(5));
    }
}
