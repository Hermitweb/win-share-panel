namespace WinShare.Core.Contracts;

/// <summary>组成员对象类型（TS: GroupMember.objectClass）。</summary>
public enum MemberObjectClass { User, Group }

/// <summary>本地用户（TS: types.ts LocalUser）。</summary>
public record LocalUser
{
    public string name { get; init; }
    public string fullName { get; init; }
    public bool enabled { get; init; }
    public string description { get; init; }
    public IReadOnlyList<string> groups { get; init; }
    public bool passwordRequired { get; init; }
    public bool passwordChangeable { get; init; }
    public bool passwordExpires { get; init; }
    public bool userMayChangePassword { get; init; }
    public string passwordLastSet { get; init; }
    public string lastLogon { get; init; }
    public string sid { get; init; }
    /// <summary>TS 侧为开放联合 'Local' | 'ActiveDirectory' | 'MicrosoftAccount' | string。</summary>
    public string principalSource { get; init; }
}

/// <summary>本地组（TS: types.ts LocalGroup）。</summary>
public record LocalGroup
{
    public string name { get; init; }
    public string description { get; init; }
    public IReadOnlyList<GroupMember> members { get; init; }
}

/// <summary>组成员（TS: types.ts GroupMember）。</summary>
public record GroupMember
{
    public string name { get; init; }
    public MemberObjectClass objectClass { get; init; }
    /// <summary>TS 侧为开放联合 'Local' | 'ActiveDirectory' | string。</summary>
    public string principalSource { get; init; }
}

/// <summary>创建用户入参（TS: user.ts CreateUserOpts）。</summary>
public record CreateUserOpts
{
    public string name { get; init; }
    public string password { get; init; }
    public string? fullName { get; init; }
    public string? description { get; init; }
    public bool? enabled { get; init; }
    public bool? passwordChangeable { get; init; }
    public bool? passwordExpires { get; init; }
}

/// <summary>更新用户入参（TS: user.ts UpdateUserOpts）。</summary>
public record UpdateUserOpts
{
    public string? fullName { get; init; }
    public string? description { get; init; }
    public bool? enabled { get; init; }
    public bool? passwordChangeable { get; init; }
    public bool? passwordExpires { get; init; }
}

/// <summary>创建组入参（TS: user.ts CreateGroupOpts）。</summary>
public record CreateGroupOpts
{
    public string name { get; init; }
    public string? description { get; init; }
}
