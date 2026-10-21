// Get-Service ... | Select-Object Name, Status, StartType 的回读解析。
// nfs.ts / ftp.ts / webdav.ts 三处 getServiceStatus 的解析逻辑逐字相同，故收敛到本类。
// 枚举数值映射：ServiceControllerStatus 1=Stopped 4=Running；ServiceStartMode 2=Automatic 3=Manual 4=Disabled
// （ConvertTo-Json 把枚举序列化成数值，但字符串形态也需兼容——TS 同时判断 typeof 与数值）。
using System.Text.Json;
using WinShare.Core.Contracts;

namespace WinShare.Core.Services.ProtocolServices;

internal static class ServiceStatusReader
{
    /// <summary>TS: if (!raw || !raw.Name) → { name, status: 'Unknown', startType: 'Unknown' }。</summary>
    public static ServiceStatus Read(JsonElement raw, string fallbackName)
    {
        JsonElement r = ConfigJson.Obj(raw);
        string? rawName = ConfigJson.Str(r, "Name");
        if (string.IsNullOrEmpty(rawName))
        {
            return new ServiceStatus { name = fallbackName, status = ServiceState.Unknown, startType = "Unknown" };
        }

        int statusNum = (int)(ConfigJson.Number(r, "Status") ?? 0);
        string statusStr = ConfigJson.Str(r, "Status") ?? "";
        ServiceState status;
        if (statusStr == "Running" || statusNum == 4) status = ServiceState.Running;
        else if (statusStr == "Stopped" || statusNum == 1) status = ServiceState.Stopped;
        else status = ServiceState.Unknown;

        int startNum = (int)(ConfigJson.Number(r, "StartType") ?? 0);
        string startType = ConfigJson.Str(r, "StartType") ?? "";
        if (startType.Length == 0)
        {
            startType = startNum switch
            {
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown",
            };
        }
        return new ServiceStatus { name = rawName, status = status, startType = startType };
    }
}
