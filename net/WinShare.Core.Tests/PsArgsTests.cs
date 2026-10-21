using WinShare.Core.Ps;

namespace WinShare.Core.Tests;

/// <summary>
/// electron/lib/powershell.test.ts 的 C# 逐例移植（安全拼接闸门 57 个断言场景）。
/// 每个测试方法 XML doc 标注 [Source: powershell.test.ts#describe/it]。
/// </summary>

public class PsQuoteTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 正常字符串用单引号包裹]</summary>
    public void NormalString_WrappedInSingleQuotes()
    {
        Assert.Equal("'test'", PsArgs.PsQuote("test"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 空字符串包裹为空引号]</summary>
    public void EmptyString_WrappedAsEmptyQuotes()
    {
        Assert.Equal("''", PsArgs.PsQuote(""));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 含单引号的字符串转义为双单引号]</summary>
    public void SingleQuote_EscapedAsDouble()
    {
        Assert.Equal("'it''s'", PsArgs.PsQuote("it's"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 含多个单引号的字符串全部转义]</summary>
    public void MultipleQuotes_AllEscaped()
    {
        Assert.Equal("'a''b''c'", PsArgs.PsQuote("a'b'c"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 注入尝试：闭合引号 + 注入命令]</summary>
    public void Injection_CloseQuoteAndCommand()
    {
        string malicious = "'; Remove-LocalUser -Name 'Administrator";
        string result = PsArgs.PsQuote(malicious);
        // 转义后内部单引号变为 ''，无法闭合外层引号
        Assert.Equal("'''; Remove-LocalUser -Name ''Administrator'", result);
        Assert.DoesNotContain("Administrator' -", result);
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 注入尝试：$ 变量插值在单引号中不生效]</summary>
    public void Injection_DollarInterpolationInertInSingleQuotes()
    {
        string malicious = @"$(Remove-Item C:\ -Recurse)";
        string result = PsArgs.PsQuote(malicious);
        // PS 单引号字符串不转义反斜杠，$ 不会被插值
        Assert.Equal(@"'$(Remove-Item C:\ -Recurse)'", result);
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 注入尝试：反引号转义在单引号中不生效]</summary>
    public void Injection_BacktickInertInSingleQuotes()
    {
        string malicious = "test`n&whoami";
        string result = PsArgs.PsQuote(malicious);
        Assert.Equal("'test`n&whoami'", result);
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 中文字符串正常包裹]</summary>
    public void ChineseString_Wrapped()
    {
        Assert.Equal("'测试共享'", PsArgs.PsQuote("测试共享"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote F-3：含空格的 Windows 路径（relaunchAsAdmin 的 exePath 场景）被单引号完整包裹]</summary>
    public void WindowsPathWithSpaces_FullyWrapped()
    {
        string exe = @"C:\Program Files\WinShare Panel\WinShare Panel.exe";
        Assert.Equal("'" + exe + "'", PsArgs.PsQuote(exe));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psQuote 数字参数自动转字符串]</summary>
    public void NumberValue_AutoConvertedToString()
    {
        Assert.Equal("'42'", PsArgs.PsQuote(42));
    }
}

public class PsEscapeSingleTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#psEscapeSingle 正常字符串不变]</summary>
    public void NormalString_Unchanged()
    {
        Assert.Equal("test", PsArgs.PsEscapeSingle("test"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEscapeSingle 含单引号转义为双单引号]</summary>
    public void Quote_EscapedAsDouble()
    {
        Assert.Equal("it''s", PsArgs.PsEscapeSingle("it's"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEscapeSingle 用于 IIS 路径构造时不会被注入]</summary>
    public void IisPathInjection_Safe()
    {
        string malicious = "test'; Remove-Website -Name '";
        string result = PsArgs.PsEscapeSingle(malicious);
        Assert.Equal("test''; Remove-Website -Name ''", result);
    }
}

public class ValidateShareNameTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#validateShareName 系统/隐藏共享尾 $ 合法]</summary>
    public void SystemHiddenShares_TrailingDollarLegal()
    {
        Assert.True(PsArgs.ValidateShareName("IPC$"));
        Assert.True(PsArgs.ValidateShareName("ADMIN$"));
        Assert.True(PsArgs.ValidateShareName("C$"));
        Assert.True(PsArgs.ValidateShareName("rdp-tcp$"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateShareName 普通名称照常放行]</summary>
    public void OrdinaryNames_Pass()
    {
        Assert.True(PsArgs.ValidateShareName("测试"));
        Assert.True(PsArgs.ValidateShareName("my-share.v1"));
        Assert.True(PsArgs.ValidateShareName("Public Share"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateShareName 中间/多个 $ 与注入形态拒绝]</summary>
    public void MiddleOrMultipleDollar_AndInjectionForms_Rejected()
    {
        Assert.False(PsArgs.ValidateShareName("A$B"));
        Assert.False(PsArgs.ValidateShareName("a$(calc)"));
        Assert.False(PsArgs.ValidateShareName("a$$"));
        Assert.False(PsArgs.ValidateShareName("IPC$x"));
        Assert.False(PsArgs.ValidateShareName("'; Get-SmbShare; '"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateShareName 空/超长/非法字符/非字符串拒绝]</summary>
    public void EmptyOrTooLongOrIllegalOrNonString_Rejected()
    {
        Assert.False(PsArgs.ValidateShareName(""));
        Assert.False(PsArgs.ValidateShareName(new string('x', 81)));
        Assert.False(PsArgs.ValidateShareName("a:b"));
        Assert.False(PsArgs.ValidateShareName(null)); // TS undefined
        Assert.False(PsArgs.ValidateShareName(42)); // TS 42 as never
    }
}

public class ValidateNameTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 合法英文名]</summary>
    public void LegalEnglishNames()
    {
        Assert.True(PsArgs.ValidateName("test"));
        Assert.True(PsArgs.ValidateName("my_share"));
        Assert.True(PsArgs.ValidateName("share.1"));
        Assert.True(PsArgs.ValidateName("share-1"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 合法中文名]</summary>
    public void LegalChineseNames()
    {
        Assert.True(PsArgs.ValidateName("测试共享"));
        Assert.True(PsArgs.ValidateName("共享1"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含空格合法]</summary>
    public void SpacesAllowed()
    {
        Assert.True(PsArgs.ValidateName("my share"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 空字符串非法]</summary>
    public void EmptyRejected()
    {
        Assert.False(PsArgs.ValidateName(""));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName null/undefined 非法]</summary>
    public void NullUndefinedRejected()
    {
        Assert.False(PsArgs.ValidateName(null));
        Assert.False(PsArgs.ValidateName((string?)null));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 超长名称非法（>80字符）]</summary>
    public void Over80CharsRejected()
    {
        Assert.False(PsArgs.ValidateName(new string('a', 81)));
        Assert.True(PsArgs.ValidateName(new string('a', 80)));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含 $ 非法（防 PowerShell 变量插值）]</summary>
    public void DollarRejected()
    {
        Assert.False(PsArgs.ValidateName("test$"));
        Assert.False(PsArgs.ValidateName("te$st"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含分号非法（防命令分隔）]</summary>
    public void SemicolonRejected()
    {
        Assert.False(PsArgs.ValidateName("test;rm"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含管道符非法]</summary>
    public void PipeRejected()
    {
        Assert.False(PsArgs.ValidateName("test|cmd"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含反斜杠非法]</summary>
    public void BackslashRejected()
    {
        Assert.False(PsArgs.ValidateName(@"test\share"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validateName 含特殊字符非法]</summary>
    public void SpecialCharsRejected()
    {
        Assert.False(PsArgs.ValidateName("test<>"));
        Assert.False(PsArgs.ValidateName("test\""));
        Assert.False(PsArgs.ValidateName("test'"));
    }
}

public class ValidatePathTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath 合法 Windows 绝对路径]</summary>
    public void LegalWindowsAbsolutePaths()
    {
        Assert.True(PsArgs.ValidatePath(@"C:\share"));
        Assert.True(PsArgs.ValidatePath(@"D:\data\folder"));
        Assert.True(PsArgs.ValidatePath("C:/share"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath 相对路径非法]</summary>
    public void RelativePathsRejected()
    {
        Assert.False(PsArgs.ValidatePath("relative/path"));
        Assert.False(PsArgs.ValidatePath("./share"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath Unix 路径非法]</summary>
    public void UnixPathRejected()
    {
        Assert.False(PsArgs.ValidatePath("/home/user"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath 含控制字符非法（首位）]</summary>
    public void ControlCharFirstSegmentRejected()
    {
        // 正则仅对路径段首字符排除控制字符，中间字符不排除——这是已知限制
        Assert.False(PsArgs.ValidatePath(@"C:\share".Insert(3, "\u0000")));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath 含非法字符非法]</summary>
    public void IllegalCharsRejected()
    {
        Assert.False(PsArgs.ValidatePath(@"C:\share<""test"""));
        Assert.False(PsArgs.ValidatePath(@"C:\share|pipe"));
        Assert.False(PsArgs.ValidatePath(@"C:\share?query"));
        Assert.False(PsArgs.ValidatePath(@"C:\share*star"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#validatePath 空字符串非法]</summary>
    public void EmptyRejected()
    {
        Assert.False(PsArgs.ValidatePath(""));
    }
}

public class PsBoolTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool true 返回 $true]</summary>
    public void True_ReturnsDollarTrue()
    {
        Assert.Equal("$true", PsArgs.PsBool(true));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool false 返回 $false]</summary>
    public void False_ReturnsDollarFalse()
    {
        Assert.Equal("$false", PsArgs.PsBool(false));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool 字符串 "true" 返回 null（非布尔）]</summary>
    public void StringTrue_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool("true"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool 数字 1 返回 null（非布尔）]</summary>
    public void One_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool(1));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool 数字 0 返回 null（非布尔）]</summary>
    public void Zero_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool(0));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool undefined 返回 null]</summary>
    public void Undefined_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool((object?)null));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool null 返回 null]</summary>
    public void Null_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool(null));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psBool 注入字符串 "$true; rm -rf" 返回 null]</summary>
    public void InjectionString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsBool("$true; rm -rf"));
    }
}

public class PsNumberTests
{
    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 正整数返回字符串]</summary>
    public void PositiveInt_ReturnsString()
    {
        Assert.Equal("42", PsArgs.PsNumber(42));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 零返回字符串]</summary>
    public void Zero_ReturnsString()
    {
        Assert.Equal("0", PsArgs.PsNumber(0));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 负数返回字符串]</summary>
    public void Negative_ReturnsString()
    {
        Assert.Equal("-1", PsArgs.PsNumber(-1));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 小数返回字符串]</summary>
    public void Fraction_ReturnsString()
    {
        Assert.Equal("3.14", PsArgs.PsNumber(3.14));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber NaN 返回 null]</summary>
    public void NaN_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber(double.NaN));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber Infinity 返回 null]</summary>
    public void Infinity_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber(double.PositiveInfinity));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 字符串 "42" 返回 null（非数字类型）]</summary>
    public void String42_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber("42"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber 注入字符串 "42; rm" 返回 null]</summary>
    public void InjectionString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber("42; rm"));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psNumber undefined 返回 null]</summary>
    public void Undefined_ReturnsNull()
    {
        Assert.Null(PsArgs.PsNumber((object?)null));
    }
}

public class PsEnumTests
{
    private static readonly IReadOnlySet<string> Allowed = new HashSet<string> { "ro", "rw", "krb5" };

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum 白名单内值原样返回]</summary>
    public void WhitelistedValues_ReturnedAsIs()
    {
        Assert.Equal("ro", PsArgs.PsEnum("ro", Allowed));
        Assert.Equal("rw", PsArgs.PsEnum("rw", Allowed));
        Assert.Equal("krb5", PsArgs.PsEnum("krb5", Allowed));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum 白名单外值返回 null]</summary>
    public void NonWhitelistedValues_ReturnNull()
    {
        Assert.Null(PsArgs.PsEnum("rx", Allowed));
        Assert.Null(PsArgs.PsEnum("admin", Allowed));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum 非字符串返回 null]</summary>
    public void NonString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsEnum(42, Allowed));
        Assert.Null(PsArgs.PsEnum(true, Allowed));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum 注入字符串返回 null（不在白名单中）]</summary>
    public void InjectionString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsEnum("ro; Remove-LocalUser 'Administrator'", Allowed));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum undefined 返回 null]</summary>
    public void Undefined_ReturnsNull()
    {
        Assert.Null(PsArgs.PsEnum((object?)null, Allowed));
    }

    [Fact]
    /// <summary>[Source: powershell.test.ts#psEnum 空字符串返回 null</summary>
    public void EmptyString_ReturnsNull()
    {
        Assert.Null(PsArgs.PsEnum("", Allowed));
    }
}


