// 移植自 electron/services/{share,smb,preset}.ts 各自的 dataDir()/presetFile()/disabledFile()/snapshotDir()。
// TS: join(process.env.APPDATA || homedir(), 'WinSharePanel')——三个服务共用同一应用数据根，
// C# 侧收敛为一个静态门面（含测试覆盖钩子，对应 TS 测试设置 process.env.APPDATA = tmpdir）。
using System.IO;

namespace WinShare.Core.Services.Shares;

/// <summary>
/// 应用数据目录（%APPDATA%\WinSharePanel）与 share/smb/preset 三个域各自的持久文件定位。
/// </summary>
public static class ShareData
{
    private static readonly object Gate = new();
    private static string? _override;

    /// <summary>测试专用：覆盖数据根（TS 测试等价于 process.env.APPDATA = mkdtempSync(...)）。</summary>
    public static void SetDirForTesting(string? dir)
    {
        lock (Gate) { _override = dir; }
    }

    /// <summary>TS: dataDir()。</summary>
    public static string DataDir()
    {
        lock (Gate)
        {
            if (_override is not null) return _override;
            string baseDir = OperatingSystem.IsWindows()
                ? (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA"))
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Environment.GetEnvironmentVariable("APPDATA")!)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(baseDir, "WinSharePanel");
        }
    }

    /// <summary>TS: share.ts disabledFile()（toggleShare 禁用记录）。</summary>
    public static string DisabledFile() => Path.Combine(DataDir(), "disabled.json");

    /// <summary>TS: preset.ts presetFile()（自定义权限预设）。</summary>
    public static string PresetFile() => Path.Combine(DataDir(), "presets.json");

    /// <summary>TS: smb.ts snapshotDir()（写前配置快照；目录不存在则创建）。</summary>
    public static string SnapshotDir()
    {
        string dir = Path.Combine(DataDir(), "smb-snapshots");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
