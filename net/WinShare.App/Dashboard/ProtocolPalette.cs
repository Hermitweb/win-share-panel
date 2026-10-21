using WinShare.Core.Contracts;

namespace WinShare.App.Dashboard;

/// <summary>协议配色 / 标签（与 src/pages/Dashboard.tsx 的 PROTOCOL_COLOR / PROTOCOL_LABEL 逐值对齐）。</summary>
public static class ProtocolPalette
{
    public static readonly Protocol[] Order = { Protocol.Smb, Protocol.Nfs, Protocol.Ftp, Protocol.Webdav };

    public static string Label(Protocol p) => p switch
    {
        Protocol.Smb => "SMB",
        Protocol.Nfs => "NFS",
        Protocol.Ftp => "FTP",
        Protocol.Webdav => "WebDAV",
        _ => p.ToString() ?? "",
    };

    /// <summary>条形图填充色（TS PROTOCOL_COLOR）。</summary>
    public static string Color(Protocol p) => p switch
    {
        Protocol.Smb => "#7EC8F0",
        Protocol.Nfs => "#B37FEB",
        Protocol.Ftp => "#73D13D",
        Protocol.Webdav => "#FFA940",
        _ => "#8C8C8C",
    };

    /// <summary>antd Tag 色系（TS PROTOCOL_TAG_COLOR，主题 token 名）。</summary>
    public static string TagColor(Protocol p) => p switch
    {
        Protocol.Smb => "blue",
        Protocol.Nfs => "purple",
        Protocol.Ftp => "green",
        Protocol.Webdav => "orange",
        _ => "default",
    };

}
