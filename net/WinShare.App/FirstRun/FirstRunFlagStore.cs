// 首启完成标记的存储实现（TS 用 localStorage；WPF 无 localStorage，
// 用等价的一次性键值文件 %APPDATA%\WinSharePanel\firstrun.flag）。
// 语义对齐 firstRun.ts：读失败/不存在 = 未标记；写失败不抛错、不阻塞向导收尾。
using System.IO;

namespace WinShare.App.FirstRun;

/// <summary>一次性「已完成初始化」标记（对齐 localStorage.getItem/setItem 语义）。</summary>
public sealed class FirstRunFlagStore
{
    private readonly string _file;

    public FirstRunFlagStore(string? pathOverride = null)
    {
        _file = pathOverride ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        string baseDir = Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "WinSharePanel", "firstrun.flag");
    }

    /// <summary>TS: readFirstRunFlag() —— 不可用/缺失返回 null（判未标记）。</summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            return File.ReadAllText(_file).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TS: markFirstRunComplete() —— 写失败返回 false，不抛错。</summary>
    public bool MarkComplete()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, FirstRunLogic.DoneValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>测试用：清除标记。</summary>
    public void ClearForTesting()
    {
        try
        {
            if (File.Exists(_file)) File.Delete(_file);
        }
        catch
        {
            // 清理失败忽略
        }
    }
}
