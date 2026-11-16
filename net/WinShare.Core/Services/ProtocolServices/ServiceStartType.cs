// 服务启动类型写回（Set-Service -StartupType）。v2.1 面板解锁：
// 服务状态条「启动类型」由纯展示升级为可改（自动/手动/禁用），写入即时落盘、下次服务启动生效。
// 四个协议服务共用：SMB=LanmanServer、NFS=NfsService/NfsClnt（运行时解析）、FTP=ftpsvc、WebDAV=W3SVC。
using System.Text.RegularExpressions;
using System.Text.Json;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;
using WinShare.Core.Services.Shares; // PsGate：PS 参数闸门窄门面

namespace WinShare.Core.Services.ProtocolServices;

internal static class ServiceStartType
{
    private static readonly Regex AccessDenied = new(
        @"access is denied|拒绝访问|administrator|权限", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Set-Service -StartupType 合法值（UI 下拉与命令白名单同源）。</summary>
    public static readonly string[] Allowed = ["Automatic", "Manual", "Disabled"];

    private static string Slice(string? s)
    {
        string v = s ?? "";
        return v.Length <= 200 ? v : v[..200];
    }

    /// <summary>
    /// 写入启动类型并回读校验。serviceName 为空视为服务未装；回读不一致 / 拒绝访问均给明确错误。
    /// </summary>
    public static async Task SetAsync(IPsInvoker ps, string? serviceName, string startType, string protocolLabel)
    {
        // 白名单收敛为精确拼写（枚举语义固定，杜绝任意 token 拼接）
        string token = startType switch
        {
            "Automatic" or "automatic" or "自动" => "Automatic",
            "Manual" or "manual" or "手动" => "Manual",
            "Disabled" or "disabled" or "禁用" => "Disabled",
            _ => throw Errors.InvalidParam("启动类型非法（仅允许 自动/手动/禁用）"),
        };
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw Errors.CommandFailed($"{protocolLabel} 服务未安装或不可识别，无法修改启动类型");
        }
        string quoted = PsGate.PsQuote(serviceName);
        try
        {
            await ps.RunVoidAsync(
                $"Set-Service -Name {quoted} -StartupType {token} -ErrorAction Stop", 15000, 0);
        }
        catch (Exception e)
        {
            if (AccessDenied.IsMatch(e.Message ?? ""))
            {
                throw Errors.CommandFailed("修改服务启动类型需要管理员权限，请以管理员身份运行 WinShare Panel");
            }
            throw Errors.CommandFailed($"修改 {protocolLabel} 服务启动类型失败：{Slice(e.Message)}");
        }
        // 回读校验（ServiceStatusReader 兼容枚举数值与字符串两种形态）
        try
        {
            JsonElement raw = await ps.RunJsonAsync(
                $"Get-Service -Name {quoted} -ErrorAction Stop | Select-Object Name, Status, StartType", 15000, 0);
            ServiceStatus status = ServiceStatusReader.Read(raw, serviceName);
            if (!string.Equals(status.startType, token, StringComparison.OrdinalIgnoreCase))
            {
                throw Errors.CommandFailed($"启动类型写入后回读不一致（当前 {status.startType}）");
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Errors.CommandFailed($"启动类型回读失败：{Slice(e.Message)}");
        }
    }
}
