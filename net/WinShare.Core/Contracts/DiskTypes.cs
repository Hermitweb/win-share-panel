namespace WinShare.Core.Contracts;

/// <summary>磁盘水位（单卷）（TS: types.ts DiskUsage）。</summary>
public record DiskUsage
{
    /// <summary>如 "E:"。</summary>
    public string drive { get; init; }
    public double freeGB { get; init; }
    public double totalGB { get; init; }
    public double freePct { get; init; }
}
