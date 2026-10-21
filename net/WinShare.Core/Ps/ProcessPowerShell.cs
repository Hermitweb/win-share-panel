// 移植自 electron/lib/powershell.ts execOnce（out-of-process 执行路径）。
//
// 为什么必须走外部进程（2026-10-21 实测结论）：
// 早期方案是「进程内 Runspace 托管」（SMA 7.5 = PowerShell 7 引擎），但 Windows 管理模块
// （SmbShare / WebAdministration / NFS）都是 **Windows PowerShell 5.1 的脚本模块（.psm1）**，
// PowerShell 7 无法直接加载，报：
//   "The 'Get-SmbServerConfiguration' command was found in the module 'SmbShare', but the module
//    could not be loaded ... running scripts is disabled on this system."
// 实测对照（本机）：powershell.exe(5.1) 执行 Get-SmbServerConfiguration 正常；SMA7.5 进程内失败。
// TS 原版也正是 spawn powershell.exe(5.1, -ExecutionPolicy Bypass) 才拿到这些命令。
// 故生产通道恢复为 TS 的 out-of-process 语义；T1 的进程内池保留（协议测试仍覆盖），但不再作默认通道。
using System.Diagnostics;
using System.Text;
using WinShare.Core.Lib;

namespace WinShare.Core.Ps;

/// <summary>Windows PowerShell 5.1 外部进程执行器（TS: execOnce）。</summary>
public static class ProcessPowerShell
{
    // TS: UTF8_PREFIX —— 逐字复制
    private const string Utf8Prefix =
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; " +
        "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; ";

    // TS: windowsHide:true + maxBuffer 10MB
    private const int MaxBufferBytes = 10 * 1024 * 1024;

    /// <summary>
    /// TS: nativePowerShellExe() —— 32 位包需用原生 64 位 PowerShell（32 位 PS 无 Get-LocalUser、IIS COM 未注册）。
    /// 取 System32 下的 Windows PowerShell 5.1（Sysnative 在 32 位进程下由系统重定向）。
    /// </summary>
    public static string ExePath
    {
        get
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system32 = Path.Combine(winDir, "System32");
            // 32 位进程访问 64 位 System32 需 Sysnative（Windows 会自动把 System32 重定向到 SysWOW64）
            if (!Environment.Is64BitProcess)
            {
                string sysnative = Path.Combine(winDir, "Sysnative");
                if (Directory.Exists(sysnative)) system32 = sysnative;
            }
            return Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        }
    }

    /// <summary>
    /// TS: execOnce(command, withJson, timeout)。
    /// withJson=true 追加 <c>| ConvertTo-Json -Depth 5 -Compress</c>（runPowerShell 用）；
    /// false 不追加（runPowerShellVoid 用）。
    /// </summary>
    public static async Task<string> ExecuteAsync(string command, bool withJson, int timeoutMs)
    {
        string fullCmd = withJson
            ? Utf8Prefix + command + " | ConvertTo-Json -Depth 5 -Compress"
            : Utf8Prefix + command;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(fullCmd));

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            throw new PsInvocationException("PowerShell 进程启动失败");
        }

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        bool exited;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            exited = false;
        }

        if (!exited)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出或无法 kill：忽略
            }
            throw new PsInvocationException("Command timed out（命令执行超时）");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (stdout.Length > MaxBufferBytes || stderr.Length > MaxBufferBytes)
        {
            throw new PsInvocationException("PowerShell 输出超过 10MB 上限");
        }
        if (proc.ExitCode != 0 || stderr.Trim().Length > 0)
        {
            // TS: execFile 在非 0 退出码时抛错，错误信息取 stderr（formatPsError 再做归一化）
            string detail = stderr.Trim().Length > 0 ? stderr : $"PowerShell 进程退出码 {proc.ExitCode}";
            throw new PsInvocationException(detail);
        }
        return stdout;
    }
}
