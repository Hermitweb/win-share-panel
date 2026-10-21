namespace WinShare.Core.Contracts;

/// <summary>账号类型（TS: SharePermission.accountType）。</summary>
public enum AccountType { User, Group }

/// <summary>共享访问级别（TS: SharePermission.access，含 NoAccess 表示显式拒绝项）。</summary>
public enum ShareAccess { Full, Change, Read, NoAccess }

/// <summary>共享权限条目（TS: types.ts SharePermission）。</summary>
public record SharePermission
{
    public string shareName { get; init; }
    public string account { get; init; }
    public AccountType accountType { get; init; }
    public ShareAccess access { get; init; }
    public bool deny { get; init; }
}

/// <summary>NTFS ACL 条目类型（TS: NtfsAclEntry.type）。</summary>
public enum NtfsAclType { Allow, Deny }

/// <summary>NTFS ACL 条目（TS: types.ts NtfsAclEntry）。</summary>
public record NtfsAclEntry
{
    public string account { get; init; }
    public string rights { get; init; }
    public NtfsAclType type { get; init; }
    public bool inherited { get; init; }
}

/// <summary>路径 NTFS 权限（TS: types.ts NtfsAcl）。</summary>
public record NtfsAcl
{
    public string path { get; init; }
    public IReadOnlyList<NtfsAclEntry> entries { get; init; }
}
