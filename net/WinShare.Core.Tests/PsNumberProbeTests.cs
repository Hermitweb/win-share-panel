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
    /// T2D 原文「string is IFormattable 恒 False」复测。实证结论（本机 .NET 10.0.13 运行时）：
    /// ① 该现象在**无任何 SMA 引用**的纯 net10.0 工程中同样复现 → 非 SMA 混编面属性，
    ///    而是本机 .NET 10 运行时的接口表事实（String 接口表无 IFormattable，Int32 有）；
    /// ② IConvertible 的接口匹配完全正常（string/boxed long/double/decimal 均命中）→
    ///    PsNumber 的 `case IConvertible` 数值分支不受影响（见上方 6 条功能探针）。
    /// 断言记录当前运行时接口表快照；若任一条变红，先核对 dotnet runtime 版本再复判。
    /// IFormattable 部分用反射 GetInterfaces 读取，规避 CS0184/CS0183 常量表达式警告。
    /// </summary>
    public void T2D_Retest_InterfaceMatchingAndTableSnapshot()
    {
        object s = "x";
        // IConvertible：PsNumber 数值分支的实际匹配点，必须命中（与 6 条功能探针互为印证）
        Assert.True(s is IConvertible, "string is IConvertible 必须为 true（IConvertible 匹配未受混编面影响）");
        Assert.True((object)42L is IConvertible, "boxed long is IConvertible 必须为 true（PsNumber 数值分支入口）");
        Assert.True((object)2.5d is IConvertible, "boxed double is IConvertible 必须为 true（PsNumber 数值分支入口）");
        Assert.True((object)3.0m is IConvertible, "boxed decimal is IConvertible 必须为 true（PsNumber 数值分支入口）");
        // IFormattable 接口表快照（.NET 10.0.13 本机观测：与 .NET 9 及更早文档相反）
        var stringIfaces = s.GetType().GetInterfaces().Select(i => i.Name).ToList();
        Assert.True(stringIfaces.Contains("IConvertible"), "String 接口表必须含 IConvertible");
        Assert.True(!stringIfaces.Contains("IFormattable"),
            "本机 .NET 10.0.13 运行时观测：String 接口表不含 IFormattable；若本条变红说明运行时已变更，先复判 runtime 版本");
        var intIfaces = typeof(int).GetInterfaces().Select(i => i.Name).ToList();
        Assert.True(intIfaces.Contains("IFormattable"),
            "本机 .NET 10.0.13 运行时观测：Int32 接口表含 IFormattable；若本条变红说明运行时已变更，先复判 runtime 版本");
    }
}
