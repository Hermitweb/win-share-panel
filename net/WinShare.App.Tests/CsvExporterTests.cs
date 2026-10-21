using WinShare.App.Dashboard;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>CSV 导出逐字段对齐 Dashboard.tsx exportCsv（列名/顺序/转义/BOM）。</summary>
public class CsvExporterTests
{
    static DashboardStats SampleStats() => new()
    {
        shareCount = 12,
        activeSessions = 7,
        openFiles = 33,
        serviceStatus = ServiceState.Running,
        topShares = new List<TopShare>
        {
            new() { name = "Docs", connections = 5, protocol = Protocol.Smb },
            new() { name = "Media", connections = 3, protocol = Protocol.Webdav },
        },
        byProtocol = new Dictionary<Protocol, ProtocolStats>
        {
            [Protocol.Smb] = new() { shares = 8, sessions = 5 },
            [Protocol.Nfs] = new() { shares = 2, sessions = 0 },
            [Protocol.Ftp] = new() { shares = 1, sessions = 1 },
            [Protocol.Webdav] = new() { shares = 1, sessions = 1 },
        },
    };

    [Fact]
    public void BuildCsv_MatchesTsColumnLayout()
    {
        var csv = CsvExporter.BuildCsv(SampleStats());

        // BOM 头部（TS: new Blob(['\uFEFF' + csv])）
        Assert.StartsWith("\uFEFF", csv);

        var lines = csv[1..].Split('\n');
        Assert.Equal("\"指标\",\"值\"", lines[0]);
        Assert.Equal("\"共享总数\",\"12\"", lines[1]);
        Assert.Equal("\"活跃会话\",\"7\"", lines[2]);
        Assert.Equal("\"打开文件\",\"33\"", lines[3]);
        Assert.Equal("\"服务状态\",\"Running\"", lines[4]);

        // 协议段（固定顺序 smb/nfs/ftp/webdav，标签大写对齐 PROTOCOL_LABEL）
        Assert.Equal("\"协议:SMB\",\"共享 8 / 会话 5\"", lines[5]);
        Assert.Equal("\"协议:NFS\",\"共享 2 / 会话 0\"", lines[6]);
        Assert.Equal("\"协议:FTP\",\"共享 1 / 会话 1\"", lines[7]);
        Assert.Equal("\"协议:WebDAV\",\"共享 1 / 会话 1\"", lines[8]);

        // 热门段
        Assert.Equal("\"热门:SMB:Docs\",\"5\"", lines[9]);
        Assert.Equal("\"热门:WebDAV:Media\",\"3\"", lines[10]);

        Assert.Equal(11, lines.Length);
    }

    [Fact]
    public void BuildCsv_MissingProtocolDefaultsToZero()
    {
        var stats = SampleStats() with
        {
            byProtocol = new Dictionary<Protocol, ProtocolStats>
            {
                [Protocol.Smb] = new() { shares = 1, sessions = 1 },
            },
        };
        var lines = CsvExporter.BuildCsv(stats)[1..].Split('\n');
        Assert.Equal("\"协议:NFS\",\"共享 0 / 会话 0\"", lines[6]);
        Assert.Equal("\"协议:FTP\",\"共享 0 / 会话 0\"", lines[7]);
        Assert.Equal("\"协议:WebDAV\",\"共享 0 / 会话 0\"", lines[8]);
    }

    [Fact]
    public void BuildCsv_EscapesQuotesInShareNames()
    {
        var stats = SampleStats() with
        {
            topShares = new List<TopShare>
            {
                new() { name = "A\"B", connections = 1, protocol = Protocol.Smb },
            },
        };
        var lines = CsvExporter.BuildCsv(stats)[1..].Split('\n');
        // 期望值 = "热门:SMB:A""B","1"（整格引号 + 内引号双写）
        Assert.Equal($"\"热门:SMB:A\"\"B\",\"1\"", lines[9]);
    }

    [Fact]
    public void BuildRows_EmptyTopShares_StillWritesProtocolSection()
    {
        var stats = SampleStats() with { topShares = new List<TopShare>() };
        var rows = CsvExporter.BuildRows(stats);
        // 表头 1 + 统计 4 + 协议 4 + 热门 0
        Assert.Equal(9, rows.Count);
    }
}
