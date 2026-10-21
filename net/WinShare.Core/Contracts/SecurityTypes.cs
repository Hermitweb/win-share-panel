namespace WinShare.Core.Contracts;

/// <summary>安全问题级别（TS: SecurityIssue.level）。</summary>
public enum SecurityLevel { Fail, Warn }

/// <summary>账号安全体检的单项问题（TS: types.ts SecurityIssue）。</summary>
public record SecurityIssue
{
    public string user { get; init; }
    public SecurityLevel level { get; init; }
    public string issue { get; init; }
}

/// <summary>账号安全体检报告（TS: types.ts SecurityReport）。</summary>
public record SecurityReport
{
    /// <summary>TS 字段 checked（C# 关键字，映射为 Checked）。</summary>
    public int Checked { get; init; }
    public IReadOnlyList<SecurityIssue> issues { get; init; }
    /// <summary>探测时间戳（毫秒）。</summary>
    public long at { get; init; }
}
