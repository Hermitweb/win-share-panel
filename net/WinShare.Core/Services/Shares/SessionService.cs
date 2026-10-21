// 移植自 electron/services/session.ts（SMB 会话与打开文件监控：
// listSessions / listOpenFiles / closeSession / closeFile）。
// 命令串从 TS 原文件 1:1 复制；closeSession/closeFile 的防御性校验逐字保留（psQuote 已兜底，此处提前拒绝明显非法值）。
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Ps;

namespace WinShare.Core.Services.Shares;

/// <summary><see cref="ISessionService"/> 实现（TS: electron/services/session.ts）。</summary>
public sealed class SessionService : ISessionService
{
    // TS: closeFile 的 fileId 校验——/^\d+$/（fileId 来自 Get-SmbOpenFile，应为数字）
    private static readonly Regex DigitsOnly = new(@"^\d+$", RegexOptions.Compiled);

    private readonly IPsInvoker _ps;

    public SessionService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: listSessions()。</summary>
    public async Task<IReadOnlyList<SmbSession>> ListSessionsAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-SmbSession", 15000, 2);
        return Enumerate(raw).Select(s => new SmbSession
        {
            // TS: `${s.ClientUserName || ''}@${s.ClientComputerName || ''}`
            clientId = $"{Str(s, "ClientUserName") ?? ""}@{Str(s, "ClientComputerName") ?? ""}",
            clientUserName = Str(s, "ClientUserName") ?? "",
            clientComputerName = Str(s, "ClientComputerName") ?? "",
            sessionStartTime = Str(s, "SessionStartTime") ?? "",
            clientOpenFiles = Int(s, "ClientOpenFiles"),
            clientIdleTime = Int(s, "ClientIdleTime"),
            bytesReceived = Long(s, "BytesReceived"),
            bytesSent = Long(s, "BytesSent"),
        }).ToList();
    }

    /// <summary>TS: listOpenFiles()。</summary>
    public async Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync()
    {
        JsonElement raw = await _ps.RunJsonAsync("Get-SmbOpenFile", 15000, 2);
        return Enumerate(raw).Select(f => new SmbOpenFile
        {
            // TS: fileId: String(f.FileId) —— 数值型 FileId 转字符串（缺失时按空串收敛）
            fileId = FileIdOf(f),
            path = Str(f, "Path") ?? "",
            clientUserName = Str(f, "ClientUserName") ?? "",
            clientComputerName = Str(f, "ClientComputerName") ?? "",
            lockCount = Int(f, "LockCount"),
            relativeOpenTime = Int(f, "RelativeOpenTime"),
        }).ToList();
    }

    /// <summary>
    /// TS: closeSession(clientUserName)——防御纵深：clientUserName 来自 IPC，
    /// 校验非空且无危险字符（psQuote 已兜底，此处提前拒绝明显非法值）。
    /// </summary>
    public async Task CloseSessionAsync(string clientUserName)
    {
        if (string.IsNullOrEmpty(clientUserName) || clientUserName.Length > 200)
        {
            throw Errors.InvalidParam("会话标识非法");
        }
        await _ps.RunVoidAsync($"Close-SmbSession -ClientUserName {PsGate.PsQuote(clientUserName)} -Force", 15000, 2);
    }

    /// <summary>
    /// TS: closeFile(fileId)——fileId 来自 Get-SmbOpenFile，应为数字；校验防注入（psQuote 已兜底）。
    /// </summary>
    public async Task CloseFileAsync(string fileId)
    {
        if (fileId is null || !DigitsOnly.IsMatch(fileId))
        {
            throw Errors.InvalidParam("文件标识非法");
        }
        await _ps.RunVoidAsync($"Close-SmbOpenFile -FileId {PsGate.PsQuote(fileId)} -Force", 15000, 2);
    }

    // === JSON 取值助手（TS 的 s.X || 0 语义） ===
    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop)
    {
        if (e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out int n))
        {
            return n;
        }
        return 0;
    }

    private static long Long(JsonElement e, string prop)
    {
        if (e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out long n))
        {
            return n;
        }
        return 0;
    }

    /// <summary>TS: String(f.FileId) —— 数字序列化为不变文化字符串（"1234"），缺失回落空串。</summary>
    private static string FileIdOf(JsonElement e)
    {
        if (e.TryGetProperty("FileId", out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetRawText();
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? "";
            }
        }
        return "";
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in el.EnumerateArray()) yield return item;
        }
        else
        {
            yield return el;
        }
    }
}
