namespace WinShare.Core.Contracts;

/// <summary>错误类别（TS: electron/lib/errors.ts ErrorCategory）。</summary>
public enum ErrorCategory { Network, Permission, Param, System, Unknown }

/// <summary>
/// 应用错误（TS: electron/lib/errors.ts AppError）。
/// code 为稳定错误码（如 NOT_ADMIN/SHARE_NOT_FOUND），category 为归类。
/// </summary>
public class AppException : Exception
{
    public string Code { get; }
    public ErrorCategory Category { get; }

    public AppException(string code, string message, ErrorCategory category = ErrorCategory.Unknown) : base(message)
    {
        Code = code;
        Category = category;
    }
}

/// <summary>错误工厂（TS: electron/lib/errors.ts Errors 静态对象）。</summary>
public static class Errors
{
    public static AppException NotAdmin() => new("NOT_ADMIN", "需要管理员权限才能执行此操作", ErrorCategory.Permission);
    public static AppException InvalidParam(string message) => new("INVALID_PARAM", message, ErrorCategory.Param);
    public static AppException ShareNotFound(string name) => new("SHARE_NOT_FOUND", $"共享 {name} 不存在", ErrorCategory.Param);
    public static AppException CommandFailed(string message) => new("COMMAND_FAILED", message, ErrorCategory.System);
    public static AppException PresetNotFound(string id) => new("PRESET_NOT_FOUND", $"预设模板 {id} 不存在", ErrorCategory.Param);
    public static AppException BuiltinProtected() => new("BUILTIN_PROTECTED", "内置模板不可删除", ErrorCategory.Param);
}
