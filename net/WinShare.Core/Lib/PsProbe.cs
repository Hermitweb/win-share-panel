using System.Text.Json;

namespace WinShare.Core.Lib;

/// <summary>
/// cmdlet 写参数集探测（v2.3）：不同 Windows 版本的同名 cmdlet 参数有增删
/// （如 Win11 26200 的 Set-SmbServerConfiguration 已无 EnableSMB3Protocol），
/// 保存前按本机实际参数集过滤，避免「找不到与参数名称匹配的参数」报错。
/// 探测失败返回 null（调用方按全部支持处理，错误照实上抛）。
/// </summary>
public static class PsProbe
{
    public static async Task<IReadOnlySet<string>?> GetCmdletParametersAsync(this IPsInvoker ps, string cmdlet)
    {
        try
        {
            JsonElement raw = await ps.RunJsonAsync(
                $"(Get-Command {cmdlet} -ErrorAction Stop).Parameters.Keys", 15000, 2);
            if (raw.ValueKind != JsonValueKind.Array) return null;
            return new HashSet<string>(
                raw.EnumerateArray().Select(p => p.GetString() ?? "").Where(s => s.Length > 0));
        }
        catch
        {
            return null;
        }
    }
}
