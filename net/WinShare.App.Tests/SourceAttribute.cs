namespace WinShare.App.Tests;

/// <summary>标注测试对应的 TS 参考源（任务书要求 [Source: 标注]）。
/// 原 Sessions/Shares/Users 三目录各持一份同构副本，现统一收口；子命名空间沿命名空间链即可解析。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class SourceAttribute : Attribute
{
    public string Source { get; }
    public SourceAttribute(string source) => Source = source;
}
