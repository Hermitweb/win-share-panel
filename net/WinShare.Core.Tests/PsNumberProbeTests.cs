using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// T1P 决定性探针测试：在「SMA 7.5.0 runtime(net9.0) + .NET 10 ref pack 混编」编译面下
/// 实证 PsNumber 的有限数字→InvariantCulture 字符串路径是否命中 IConvertible 分支。
/// 语义基准：electron/lib/powershell.ts 的 psNumber（typeof number &amp;&amp; Number.isFinite → String(v)，否则 null）。
/// 若全部通过 → T2D 所称「接口模式匹配恒 False」在 PsNumber 实际入参路径上不成立，不改任何生产代码。
/// </summary>
public class PsNumberProbeTests
{
    [Fact]
    /// <summary>long 42L 走 IConvertible 分支 → "42"（TS: Number.isFinite(42) → "42"）</summary>
    public void Long_42_Returns_42()
    {
        Assert.Equal("42", PsArgs.PsNumber(42L));
    }

    [Fact]
    /// <summary>double 2.5d 走 IConvertible 分支 → "2.5"（InvariantCulture，非 "2,5"）</summary>
    public void Double_2_5_Returns_2_5()
    {
        Assert.Equal("2.5", PsArgs.PsNumber(2.5d));
    }

    [Fact]
    /// <summary>decimal 3.0m 走 IConvertible 分支 → "3.0"（有限 decimal 不被前三 case 拦截）</summary>
    public void Decimal_3_0_Returns_3_0()
    {
        Assert.Equal("3.0", PsArgs.PsNumber(3.0m));
    }

    [Fact]
    /// <summary>float.NaN 命中 `case float f when IsNaN` 前置分支 → null（TS: Number.isFinite(NaN)=false）</summary>
    public void FloatNaN_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber(float.NaN));
    }

    [Fact]
    /// <summary>非数字字符串被 `case string` 前置拦截 → null（TS: typeof "x" !== "number"）</summary>
    public void NonNumericString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber("x"));
    }

    [Fact]
    /// <summary>null（TS undefined）→ null</summary>
    public void Null_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber((object?)null));
    }

    [Fact]
    /// <summary>
    /// T2D 原文「string is IFormattable 恒 False」复测：实测 + 诊断（失败消息自带全量证据：
    /// 各 `is` 判定结果、接口类型所属程序集、string 运行时接口表）。
    /// </summary>
    public void T2D_Retest_InterfacePatternMatchingInDefaultContext()
    {
        object s = "x";
        bool sFmt = s is IFormattable;
        bool sConv = s is IConvertible;
        bool lConv = (object)42L is IConvertible;
        bool dConv = (object)2.5d is IConvertible;
        bool mConv = (object)3.0m is IConvertible;
        bool iFmt = 42 is IFormattable;
        string ifaces = string.Join(";", s.GetType().GetInterfaces().Select(i => $"{i.Name}@{i.Assembly.GetName().Name}"));
        string diag = $"string is IFormattable={sFmt}, int is IFormattable={iFmt}, " +
                      $"string is IConvertible={sConv}, boxed long/double/decimal is IConvertible={lConv}/{dConv}/{mConv}; " +
                      $"IFormattable@{typeof(IFormattable).Assembly.GetName().Name}, IConvertible@{typeof(IConvertible).Assembly.GetName().Name}; " +
                      $"string runtime ifaces: {ifaces}";
        Assert.True(sFmt && sConv && lConv && dConv && mConv,
            $"BCL 接口模式匹配诊断（.NET 10 标准面下五项均应 true）：{diag}");
    }
}
