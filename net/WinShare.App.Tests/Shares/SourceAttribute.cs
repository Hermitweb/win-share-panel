namespace WinShare.App.Tests.Shares;

/// <summary>标注测试对应的 TS 参考源（任务书要求 [Source: 标注]；与 Users/Sessions 目录同惯例）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class SourceAttribute : Attribute
{
    public string Source { get; }
    public SourceAttribute(string source) => Source = source;
}
