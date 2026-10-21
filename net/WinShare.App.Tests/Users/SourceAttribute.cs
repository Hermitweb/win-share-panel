namespace WinShare.App.Tests.Users;

/// <summary>标注测试对应的 TS 参考源（任务书要求 [Source: 标注]；对齐 T3a 测试惯例）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class SourceAttribute : Attribute
{
    public string Source { get; }
    public SourceAttribute(string source) => Source = source;
}
