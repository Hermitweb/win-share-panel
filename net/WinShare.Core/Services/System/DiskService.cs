// 移植自 electron/services/disk.ts（磁盘水位 + 共享根建议：文件服务器刚需）。
// PS 命令串从 TS 原文件逐字复制；单对象/数组形状归一、total=0 不出现 NaN、
// suggestShareRoot 三级优先（非系统盘剩余最大 → 用户 Shared/共享的 → 系统盘）语义保留。
// 环境依赖：TS 的 process.env.SystemDrive 经 <see cref="SystemDrive"/> 可注入（测试对应 vi.stubEnv）。
using System.Text.Json;
using System.Text.RegularExpressions;
using WinShare.Core.Contracts;
using WinShare.Core.Lib;

namespace WinShare.Core.Services.Host;

/// <summary>IDiskService 实现。</summary>
public sealed class DiskService : IDiskService
{
    /// <summary>TS: /^[A-Za-z]$/ —— 单字母盘符过滤。</summary>
    private static readonly Regex SingleLetter = new(@"^[A-Za-z]$", RegexOptions.CultureInvariant);
    /// <summary>TS: /^([A-Za-z]):/ —— driveOfPath 盘符提取。</summary>
    private static readonly Regex DrivePrefix = new(@"^([A-Za-z]):", RegexOptions.CultureInvariant);
    /// <summary>TS: /^[A-Z]:$/ —— suggestShareRoot 的固定盘过滤。</summary>
    private static readonly Regex FixedDrive = new(@"^[A-Z]:$", RegexOptions.CultureInvariant);
    /// <summary>TS: /^\\\\[^\\]/ —— UNC 路径判定。</summary>
    private static readonly Regex UncPrefix = new(@"^\\\\[^\\]", RegexOptions.CultureInvariant);

    private readonly IPsInvoker _ps;

    /// <summary>测试注入点：TS vi.stubEnv('SystemDrive', ...) 等价；null 走真实环境变量。</summary>
    public string? SystemDriveOverride { get; set; }

    /// <summary>测试注入点：TS mockedExistsSync（用户目录探测）；null 走真实 Directory.Exists。</summary>
    public Func<string, bool>? ExistsOverride { get; set; }

    /// <summary>测试注入点：TS homedir()；null 走真实 Environment.GetFolderPath(UserProfile)。</summary>
    public Func<string>? UserHomeOverride { get; set; }

    public DiskService(IPsInvoker? ps = null) => _ps = ps ?? PsRunner.Default;

    /// <summary>TS: systemDrive() = (process.env.SystemDrive || 'C:').replace(/\\/g,'').toUpperCase()。</summary>
    private string SystemDrive
    {
        get
        {
            string raw = SystemDriveOverride
                ?? Environment.GetEnvironmentVariable("SystemDrive")
                ?? "C:";
            return raw.Replace("\\", "").ToUpperInvariant();
        }
    }

    private bool Exists(string path) => ExistsOverride?.Invoke(path) ?? Directory.Exists(path);

    private string UserHome => UserHomeOverride?.Invoke()
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// TS: getDiskUsages() —— Get-PSDrive 查询 + 脏数据防御：
    /// 单字母盘符才收；Free/Used 非有限正数一律归零（防 UI 出现 "NaN GB"）；freePct 在 total=0 时取 100。
    /// </summary>
    public async Task<IReadOnlyList<DiskUsage>> GetDiskUsagesAsync()
    {
        // TS: runPowerShell<RawDrive | RawDrive[]>('Get-PSDrive -PSProvider FileSystem | Select-Object Name,Free,Used', { retries: 0 })
        JsonElement raw = await _ps.RunJsonAsync(
            "Get-PSDrive -PSProvider FileSystem | Select-Object Name,Free,Used",
            PsRunner.DefaultTimeoutMs, 0);
        var arr = SystemJsCompat.ToArray(raw);

        var result = new List<DiskUsage>();
        foreach (JsonElement d in arr)
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            // TS: d && typeof d.Name === 'string' && /^[A-Za-z]$/.test(d.Name)
            string? name = SystemJsCompat.NonEmptyStringProp(d, "Name");
            if (name is null || !SingleLetter.IsMatch(name)) continue;

            double free = ToFiniteGB(d, "Free");
            double used = ToFiniteGB(d, "Used");
            double total = free + used;
            result.Add(new DiskUsage
            {
                drive = $"{name.ToUpperInvariant()}:",
                freeGB = Round1(free),
                totalGB = Round1(total),
                freePct = total > 0 ? JsRound((free / total) * 100) : 100,
            });
        }
        return result;
    }

    /// <summary>TS: toFiniteGB(v) = Number.isFinite(n) && n &gt; 0 ? n : 0。</summary>
    private static double ToFiniteGB(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement v)) return 0;
        double n = SystemJsCompat.NumberOf(v);
        return SystemJsCompat.IsFiniteNumber(n) && n > 0 ? n : 0;
    }

    /// <summary>TS: round1(bytes) = Math.round((bytes / 1e9) * 10) / 10。</summary>
    private static double Round1(double bytes) => JsRound((bytes / 1e9) * 10) / 10;

    private static double JsRound(double v) => SystemJsCompat.JsMathRound(v);

    /// <summary>TS: driveOfPath(p)（"E:\share\x" → "E:"），UNC/非法/空/脏值 → null。</summary>
    public string? DriveOfPath(string p)
    {
        // TS: const m = /^([A-Za-z]):/.exec(p ?? '')
        if (p is null) return null;
        Match m = DrivePrefix.Match(p);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:" : null;
    }

    /// <summary>TS: isUnc(p) = /^\\\\[^\\]/.test(p ?? ')。</summary>
    public bool IsUnc(string p) => p is not null && UncPrefix.IsMatch(p);

    /// <summary>
    /// TS: suggestShareRoot(usages, userHome='') —— 三级优先：
    /// ① 非系统固定盘里剩余最大者 → `{drive}\Share`；
    /// ② 用户目录下已存在的 Shared / 共享的；
    /// ③ 系统盘根 Share；无任何盘时兜底系统盘。
    /// </summary>
    public string SuggestShareRoot(IReadOnlyList<DiskUsage> usages, string userHome = "")
    {
        string sys = SystemDrive;
        // TS: usages.filter(d => /^[A-Z]:$/.test(d.drive) && d.drive !== sys).sort((a,b) => b.freeGB - a.freeGB)
        var nonSystem = usages
            .Where(d => FixedDrive.IsMatch(d.drive ?? "") && d.drive != sys)
            .OrderByDescending(d => d.freeGB)
            .ToList();
        if (nonSystem.Count > 0) return $"{nonSystem[0].drive}\\Share";

        if (!string.IsNullOrEmpty(userHome))
        {
            // TS: for (const name of ['Shared', '共享的']) { if (existsSync(join(home, name))) return join(home, name) }
            foreach (string name in new[] { "Shared", "共享的" })
            {
                string p = Path.Combine(userHome, name);
                if (Exists(p)) return p;
            }
        }

        // TS: const fallback = usages.find(d => d.drive === sys) ?? usages[0]
        DiskUsage? fallback = usages.FirstOrDefault(d => d.drive == sys) ?? usages.FirstOrDefault();
        return fallback is not null ? $"{fallback.drive}\\Share" : $"{sys}\\Share";
    }

    /// <summary>
    /// TS: suggestRoot() —— 查盘失败也返回可用默认值（向导不该被磁盘探测卡住）。
    /// </summary>
    public async Task<string> SuggestRootAsync()
    {
        IReadOnlyList<DiskUsage> usages = Array.Empty<DiskUsage>();
        try
        {
            usages = await GetDiskUsagesAsync();
        }
        catch
        {
            // TS: catch {} —— 静默降级为空表
        }
        return SuggestShareRoot(usages, UserHome);
    }
}
