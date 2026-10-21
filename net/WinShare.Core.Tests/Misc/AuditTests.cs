// TS: audit.ts 无对应用例 —— 最小冒烟（落盘可读、detail 省略、缺文件空串）。
using System.Text.Json;
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Misc;

public class AuditTests
{
    // [Source: 新增（audit.ts 无 TS 测试）] audit 写入后整读含全部记录字段；detail 为 null 时键省略（对齐 JSON.stringify 语义）
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        string dir = TestTmp.New("wsaudit");
        Audit.SetDirForTesting(dir);
        try
        {
            Audit.Write("alice", "create", @"C$", Audit.Success, "detail-x");
            Audit.Write("bob", "delete", "Data", Audit.Failure);
            string[] lines = Audit.ReadAuditLog().Trim().Split('\n');
            Assert.Equal(2, lines.Length);
            // JSONL：每条记录各占一行（对齐 TS 逐行 appendFileSync）
            using var doc1 = JsonDocument.Parse(lines[0]);
            using var doc2 = JsonDocument.Parse(lines[1]);
            JsonElement first = doc1.RootElement;
            JsonElement second = doc2.RootElement;
            Assert.Equal("alice", first.GetProperty("operator").GetString());
            Assert.Equal("create", first.GetProperty("action").GetString());
            Assert.Equal(@"C$", first.GetProperty("target").GetString());
            Assert.Equal("success", first.GetProperty("result").GetString());
            Assert.Equal("detail-x", first.GetProperty("detail").GetString());
            Assert.True(first.TryGetProperty("ts", out _));
            Assert.Equal("failure", second.GetProperty("result").GetString());
            Assert.False(second.TryGetProperty("detail", out _), "detail=null 时应省略该键（TS JSON.stringify 语义）");
        }
        finally
        {
            Audit.SetDirForTesting(null);
        }
    }

    // [Source: 新增（audit.ts 无 TS 测试）] 文件不存在 → 空串
    [Fact]
    public void ReadMissingFile_ReturnsEmptyString()
    {
        string dir = TestTmp.New("wsaudit");
        Audit.SetDirForTesting(dir);
        try
        {
            Assert.Equal("", Audit.ReadAuditLog());
        }
        finally
        {
            Audit.SetDirForTesting(null);
        }
    }
}
