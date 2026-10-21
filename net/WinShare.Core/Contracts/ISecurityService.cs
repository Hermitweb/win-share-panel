namespace WinShare.Core.Contracts;

/// <summary>
/// 账号安全体检（对应 electron/services/security.ts 导出：securityReport）。
/// NTFS 权限读取在 IUserService.GetNtfsPermissionsAsync（user.ts getNtfsPermissions）。
/// </summary>
public interface ISecurityService
{
    Task<SecurityReport> GetSecurityReportAsync();
}
