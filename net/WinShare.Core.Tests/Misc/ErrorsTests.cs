// TS: errors.ts 无对应用例 —— 最小冒烟。
// 说明：errors.ts 的移植已在冻结的 Contracts/ErrorTypes.cs（Errors + AppException + ErrorCategory，
// 消息逐字一致）落地，T2D 的 Lib 层不重复移植（同名类会在双 using 下产生 CS0104 歧义）；
// 本冒烟针对该既有移植面做断言，避免"零用例覆盖"。
using WinShare.Core.Contracts;

namespace WinShare.Core.Tests.Misc;

public class ErrorsTests
{
    // [Source: 新增（errors.ts 移植面 = Contracts/ErrorTypes.cs）] NotAdmin：code/category/消息逐字对齐
    [Fact]
    public void NotAdmin_HasStableCodeAndMessage()
    {
        AppException e = Errors.NotAdmin();
        Assert.Equal("NOT_ADMIN", e.Code);
        Assert.Equal(ErrorCategory.Permission, e.Category);
        Assert.Equal("需要管理员权限才能执行此操作", e.Message);
        Assert.IsAssignableFrom<Exception>(e);
    }

    // [Source: 新增（errors.ts 移植面 = Contracts/ErrorTypes.cs）] InvalidParam：消息透传
    [Fact]
    public void InvalidParam_PassesMessageThrough()
    {
        AppException e = Errors.InvalidParam("导入内容必须是文本");
        Assert.Equal("INVALID_PARAM", e.Code);
        Assert.Equal(ErrorCategory.Param, e.Category);
        Assert.Equal("导入内容必须是文本", e.Message);
    }

    // [Source: 新增（errors.ts 移植面 = Contracts/ErrorTypes.cs）] ShareNotFound / PresetNotFound 插值消息
    [Fact]
    public void ShareNotFoundAndPresetNotFound_InterpolateNames()
    {
        Assert.Equal("共享 Data 不存在", Errors.ShareNotFound("Data").Message);
        Assert.Equal("预设模板 p1 不存在", Errors.PresetNotFound("p1").Message);
    }

    // [Source: 新增（errors.ts 移植面 = Contracts/ErrorTypes.cs）] CommandFailed / BuiltinProtected
    [Fact]
    public void CommandFailedAndBuiltinProtected()
    {
        AppException cf = Errors.CommandFailed("oops");
        Assert.Equal("COMMAND_FAILED", cf.Code);
        Assert.Equal(ErrorCategory.System, cf.Category);
        Assert.Equal("oops", cf.Message);

        AppException bp = Errors.BuiltinProtected();
        Assert.Equal("BUILTIN_PROTECTED", bp.Code);
        Assert.Equal(ErrorCategory.Param, bp.Category);
        Assert.Equal("内置模板不可删除", bp.Message);
    }
}
