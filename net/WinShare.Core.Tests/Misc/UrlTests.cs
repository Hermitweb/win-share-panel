// 移植自 electron/lib/url.test.ts（TS: url.ts isSafeExternalUrl → C#: UrlGuard.IsSafeExternalUrl）。
using WinShare.Core.Lib;

namespace WinShare.Core.Tests.Misc;

public class UrlTests
{
    // [Source: url.test.ts#放行 http/https（大小写不敏感）]
    [Fact]
    public void AllowsHttpHttpsCaseInsensitive()
    {
        Assert.True(UrlGuard.IsSafeExternalUrl("https://github.com/Hermitweb/win-share-panel/releases/latest"));
        Assert.True(UrlGuard.IsSafeExternalUrl("http://127.0.0.1:8080/x"));
        Assert.True(UrlGuard.IsSafeExternalUrl("HTTPS://EXAMPLE.COM"));
    }

    // [Source: url.test.ts#拒绝非 http/https 协议（这是防 shell 被诱导打开的关键）]
    [Fact]
    public void RejectsNonHttpProtocols()
    {
        Assert.False(UrlGuard.IsSafeExternalUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.False(UrlGuard.IsSafeExternalUrl("javascript:alert(1)"));
        Assert.False(UrlGuard.IsSafeExternalUrl("ms-msdt:/id PCWDiagnostic"));
        Assert.False(UrlGuard.IsSafeExternalUrl(@"\\attacker\share\evil.exe"));
        Assert.False(UrlGuard.IsSafeExternalUrl(@"C:\Windows\System32\cmd.exe"));
    }

    // [Source: url.test.ts#拒绝非字符串与空串]
    // 说明：TS 的 unknown 入参（42 / {toString}）在 C# 由 string? 类型系统天然覆盖，等价断言为 null。
    [Fact]
    public void RejectsNonStringAndEmpty()
    {
        Assert.False(UrlGuard.IsSafeExternalUrl(null));
        Assert.False(UrlGuard.IsSafeExternalUrl(""));
    }

    // [Source: url.test.ts#拒绝前导空白（不 trim：空白会改变 shell 解析语义）]
    [Fact]
    public void RejectsLeadingWhitespace()
    {
        Assert.False(UrlGuard.IsSafeExternalUrl(" https://example.com"));
        Assert.False(UrlGuard.IsSafeExternalUrl("\thttps://example.com"));
    }

    // [Source: url.test.ts#拒绝超长 URL（限制长度，避免把巨串交给 shell）]
    [Fact]
    public void RejectsOversizedUrl()
    {
        Assert.False(UrlGuard.IsSafeExternalUrl("https://example.com/" + new string('a', 2000)));
        Assert.True(UrlGuard.IsSafeExternalUrl("https://example.com/" + new string('a', 100)));
    }
}
