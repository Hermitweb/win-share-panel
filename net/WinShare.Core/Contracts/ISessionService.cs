namespace WinShare.Core.Contracts;

/// <summary>
/// SMB 会话与打开文件监控（对应 electron/services/session.ts 导出：
/// listSessions / listOpenFiles / closeSession / closeFile）。
/// </summary>
public interface ISessionService
{
    /// <summary>TS: listSessions()。</summary>
    Task<IReadOnlyList<SmbSession>> ListSessionsAsync();

    /// <summary>TS: listOpenFiles()。</summary>
    Task<IReadOnlyList<SmbOpenFile>> ListOpenFilesAsync();

    /// <summary>TS: closeSession(clientUserName)。</summary>
    Task CloseSessionAsync(string clientUserName);

    /// <summary>TS: closeFile(fileId)。</summary>
    Task CloseFileAsync(string fileId);
}
