using WinShare.App.Events;
using WinShare.App.Shares;
using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 共享管理主 VM 行为测试（对齐 Shares.tsx：筛选/置顶/批量/导入导出/向导/能力横幅；
/// stub 契约注入，服务缺席走占位降级路径）。
/// </summary>
public class SharesViewModelTests
{
    readonly string _stateDir;

    public SharesViewModelTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), "winshare-tests-" + Guid.NewGuid("n"));
        Directory.CreateDirectory(_stateDir);
    }

    FakeShareService _share = new();
    FakePresetService _preset = new();
    FakeUserService _user = new();
    FakeDiskService _disk = new();
    FakeSharesFeed _feed = new();
    List<(string name, byte[] bytes)> _exports = new();
    SharesViewModel _vm = null!;

    void MakeVm()
    {
        _feed = new FakeSharesFeed
        {
            Shares = new List<Share>
            {
                TestShares.Smb("Docs", concurrent: 2),
                TestShares.Smb("Music", concurrent: 0, path: @"D:\Music"),
            },
            Disks = new List<DiskUsage>
            {
                new() { drive = "E:", freeGB = 1, totalGB = 100, freePct = 1 },
                new() { drive = "D:", freeGB = 50, totalGB = 100, freePct = 50 },
            },
        };
        _vm = new SharesViewModel(
            _feed,
            new AppSettingsStore(_stateDir),
            new AppEventBus(),
            _share, _preset, protocol: null, system: null, _user, _disk,
            async (name, bytes) => _exports.Add((name, bytes)),
            () => Task.FromResult(_importPickerReturnsNull ? (string?)null : _importText));
    }
    string _importText = "{}";

    Share SmbShare(string name) => _vm.Rows.First(r => r.Share.name == name).Share;

    // ===== 列表/筛选/置顶 [Source: Shares.tsx#filtered] =====

    [Fact]
    public async Task Load_ListsAllWithPinnedFirst()
    {
        MakeVm();
        _preset.Presets.Add(new PermissionPreset
        {
            id = "p1", name = "模板", description = "d", builtIn = false, entries = Array.Empty<PresetEntry>(),
        });
        await _vm.LoadAsync();
        Assert.Equal(2, _vm.Rows.Count);
        // 默认顺序
        Assert.Equal("Docs", _vm.Rows[0].Share.name);
        _vm.TogglePin("smb:Music");
        Assert.Equal("Music", _vm.Rows[0].Share.name); // 置顶恒排最前
        Assert.True(_vm.Rows[0].Pinned);
    }

    [Fact]
    public async Task Filter_KeywordMatchesNamePathDescription()
    {
        MakeVm();
        await _vm.LoadAsync();
        _vm.Keyword = "music";
        Assert.Single(_vm.Rows);
        Assert.Equal("Music", _vm.Rows[0].Share.name);
        _vm.Keyword = "D:\\Music"; // 路径匹配（小写化包含）
        _vm.Keyword = "d:\\music";
        Assert.Single(_vm.Rows);
        _vm.Keyword = "";
        _vm.OnlyConnected = true;
        Assert.Single(_vm.Rows);
        Assert.Equal("Docs", _vm.Rows[0].Share.name); // 仅 concurrentUsers>0
    }

    [Fact]
    public async Task WaterTag_OnlyOnLowDrive()
    {
        MakeVm();
        await _vm.LoadAsync();
        var docs = _vm.Rows.First(r => r.Share.name == "Docs");
        var music = _vm.Rows.First(r => r.Share.name == "Music");
        Assert.Equal("剩 1 GB", docs.WaterTagText); // E: 剩余 1GB = danger 打黄标
        Assert.Equal("danger", docs.WaterLevel);
        Assert.Equal("E: 剩余 1 GB / 共 100 GB（告警线 20 GB）", docs.WaterTip);
        Assert.Equal("", music.WaterTagText); // D: 50% 健康不打扰
    }

    // ===== 批量删除（allSettled + 聚合提示）[Source: Shares.tsx#confirmBatchDelete] =====

    [Fact]
    public async Task BatchDelete_AllSucceed()
    {
        MakeVm();
        await _vm.LoadAsync();
        _vm.SetSelection(new[] { "smb:Docs", "smb:Music" });
        Assert.True(_vm.HasSelection);
        Assert.Equal(2, _vm.SelectedCount);
        var msg = await _vm.ExecuteBatchDeleteAsync();
        Assert.Equal("已删除 2 个共享", msg);
        Assert.Equal(new[] { "Docs", "Music" }, _share.Deleted.OrderBy(x => x));
        Assert.False(_vm.HasSelection); // 删除后清空选择
        Assert.Equal("", _vm.StatusMsg); // 全成功不告警
        Assert.False(_vm.StatusIsWarning);
    }

    [Fact]
    public async Task BatchDelete_PartialFailureAggregates()
    {
        MakeVm();
        await _vm.LoadAsync();
        _vm.SetSelection(new[] { "smb:Docs", "smb:Music" });
        _share.FailOn.Add("Music"); // 第二个失败
        var msg = await _vm.ExecuteBatchDeleteAsync();
        Assert.Equal("1 个成功，1 个失败", msg);
        Assert.True(_vm.StatusIsWarning);
        Assert.Single(_share.Deleted);
    }

    [Fact]
    public async Task BatchDeleteImpactText_UsesSelectedShares()
    {
        MakeVm();
        _vm = new SharesViewModel(
            _feed, new AppSettingsStore(_stateDir), new AppEventBus(),
            _share, _preset, null, null, _user, _disk,
            async (_, _) => { }, () => Task.FromResult<string?>(null));
        await _vm.LoadAsync();
        _vm.SetSelection(new[] { "smb:Docs" }); // Docs 有 2 连接
        Assert.True(_vm.BatchDeleteImpactDanger);
        Assert.Contains("当前 2 个连接", _vm.BatchDeleteImpactText);
        Assert.Contains("操作回收站", _vm.BatchDeleteUndoHint);
    }

    // ===== 批量启停（未找到 = not found 失败项）[Source: Shares.tsx#shareToggleTick] =====

    [Fact]
    public async Task BatchToggle_SuccessAndNotFound()
    {
        MakeVm();
        await _vm.LoadAsync();
        _vm.SetSelection(new[] { "smb:Docs", "smb:Ghost" }); // Ghost 不在列表
        var msg = await _vm.ExecuteBatchToggleAsync();
        Assert.Equal("1 个成功，1 个失败", msg);
        var toggled = _share.Toggled.Single();
        Assert.Equal("Docs", toggled.name);
        Assert.False(toggled.enabled); // 原 Enabled → 切换到禁用
    }

    [Fact]
    public async Task BatchToggle_AllSuccess()
    {
        MakeVm();
        await _vm.LoadAsync();
        _vm.SetSelection(new[] { "smb:Docs", "smb:Music" });
        var msg = await _vm.ExecuteBatchToggleAsync();
        Assert.Equal("已切换 2 个共享", msg);
    }

    // ===== 行内单操作（文案逐字）[Source: Shares.tsx#handleDelete/handleToggle] =====

    [Fact]
    public async Task DeleteShare_SingleSuccess()
    {
        MakeVm();
        await _vm.LoadAsync();
        var row = _vm.Rows.First(r => r.Share.name == "Docs");
        var msg = await _vm.DeleteShareAsync(row);
        Assert.Equal("已删除", msg);
        Assert.Equal("已删除", _vm.StatusMsg);
    }

    [Fact]
    public async Task ToggleShare_EnabledBecomesDisabled()
    {
        MakeVm();
        await _vm.LoadAsync();
        var row = _vm.Rows.First(r => r.Share.name == "Docs");
        Assert.Equal("禁用", row.ToggleVerb);
        var msg = await _vm.ToggleShareAsync(row);
        Assert.Equal("已禁用", msg);
        Assert.False(_share.Toggled.Single().enabled);
    }

    // ===== 导入导出（护栏 + 文案 + 文件保存）[Source: Shares.tsx#handleExport/importProps] =====

    [Fact]
    public async Task Export_SavesJsonAndReportsSmbOnly()
    {
        MakeVm();
        _share.ExportJson = "{\"shares\":[]}";
        var msg = await _vm.ExportConfigAsync();
        Assert.Equal("", msg);
        var (name, bytes) = _exports.Single();
        Assert.StartsWith("winshare-export-", name);
        Assert.EndsWith(".json", name);
        Assert.Equal("{\"shares\":[]}", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("已导出（仅 SMB）", _vm.StatusMsg);
    }

    [Fact]
    public async Task Import_InvalidJsonReportsGuardMessage()
    {
        MakeVm();
        _importText = "{oops";
        var msg = await _vm.ImportConfigAsync();
        Assert.Equal("导入文件格式错误：JSON 解析失败", msg);
        Assert.Equal("导入文件格式错误：JSON 解析失败", _vm.StatusMsg);
        Assert.True(_vm.StatusIsWarning);
    }

    [Fact]
    public async Task Import_ValidWithSkipsBuildsWarningText()
    {
        MakeVm();
        _importText = "{\"shares\":[]}";
        _share.ImportReport = new ImportReport
        {
            imported = 1, skipped = 2,
            errors = new[] { "a: x", "b: y", "c: z" },
        };
        await _vm.ImportConfigAsync();
        Assert.Equal("导入完成：成功 1 个，跳过 2 个。a: x；b: y 等", _vm.StatusMsg);
        Assert.True(_vm.StatusIsWarning);
    }

    [Fact]
    public async Task Import_UserCanceledIsNoOp()
    {
        MakeVm();
        _importText = null!;
        _importPickerReturnsNull = true;
        var msg = await _vm.ImportConfigAsync();
        Assert.Equal("", msg);
        Assert.Equal("", _vm.StatusMsg);
    }

    bool _importPickerReturnsNull;

    // ===== 新建向导（preset 应用 + 复位 + 新手收敛）[Source: Shares.tsx#handleCreate] =====

    [Fact]
    public async Task Create_SmbWithPresetAppliesOverwrite()
    {
        MakeVm();
        _preset.Presets.Add(new PermissionPreset
        {
            id = "p1", name = "模板", description = "d", builtIn = false, entries = Array.Empty<PresetEntry>(),
        });
        var form = _vm.CreateForm;
        form.Protocol = "smb";
        form.Name = "NewShare";
        form.Path = @"E:\NewShare";
        form.PresetId = "p1";
        var msg = await _vm.CreateShareAsync();
        Assert.Equal("", msg);
        Assert.Contains("NewShare", _share.Created.Select(c => c.name));
        var applied = _preset.Applied.Single();
        Assert.Equal("NewShare", applied.share);
        Assert.Equal("p1", applied.preset);
        Assert.Equal(ApplyPresetMode.Overwrite, applied.mode);
        Assert.False(_vm.CreateWizardOpen); // 创建成功关向导
        Assert.Equal("", form.Name);        // 表单已复位
    }

    [Fact]
    public async Task Create_NoviceModeDropsAdvancedValues()
    {
        MakeVm();
        var store = new AppSettingsStore(_stateDir);
        store.SaveState(new AppStatePatch { advancedMode = false });
        _vm = new SharesViewModel(
            _feed, store, new AppEventBus(),
            _share, _preset, null, null, _user, _disk,
            async (_, _) => { }, () => Task.FromResult<string?>(null));
        var form = _vm.CreateForm;
        form.Protocol = "smb";
        form.Name = "n"; form.Path = @"E:\n";
        form.Encrypted = true; form.EncryptData = true; // 残留值（上次专家模式）
        await _vm.CreateShareAsync();
        var opts = _share.Created.Single().opts;
        Assert.Null(opts.encrypted);   // 隐藏项不提交（静默入库 = 替用户做主）
        Assert.Null(opts.encryptData);
    }

    [Fact]
    public async Task Create_ServiceAbsentReportsPlaceholder()
    {
        MakeVm();
        _vm = new SharesViewModel(
            _feed, new AppSettingsStore(_stateDir), new AppEventBus(),
            share: null, _preset, protocol: null, system: null, _user, _disk,
            async (_, _) => { }, () => Task.FromResult<string?>(null));
        var form = _vm.CreateForm;
        form.Protocol = "nfs"; // 无协议路由 + 非 SMB → 降级路径
        form.Name = "n"; form.Path = @"E:\n";
        var msg = await _vm.CreateShareAsync();
        Assert.Equal("创建共享需要契约服务（未接入）", msg);
        Assert.Equal(0, _share.Created.Count);
    }

    [Fact]
    public async Task CreateWizard_SuggestsRootWhenPathEmpty()
    {
        MakeVm();
        _disk.SuggestRootResult = @"E:\SmartRoot";
        await _vm.OpenCreateWizardAsync();
        Assert.True(_vm.CreateWizardOpen);
        Assert.Equal(@"E:\SmartRoot", _vm.CreateForm.Path);
        Assert.Equal("SmartRoot", _vm.CreateForm.Name); // 路径带名
    }

    [Fact]
    public void DropFolders_FirstPathFillsFormAndOpensWizard()
    {
        MakeVm();
        _vm.DropFolders(new[] { @"E:\Dropped", @"E:\Second" });
        Assert.Equal(@"E:\Dropped", _vm.CreateForm.Path);
        Assert.Equal("Dropped", _vm.CreateForm.Name);
        Assert.True(_vm.CreateWizardOpen);
    }

    // ===== 协议能力横幅（细分徽标）[Source: ProtocolCapabilityBanner.tsx#renderStatusBadges] =====

    [Fact]
    public async Task Banner_IisRoleBadgesFromHintRegex()
    {
        MakeVm();
        _feed.Caps = new Dictionary<Protocol, ProtocolFeatureState>
        {
            [Protocol.Ftp] = new()
            {
                protocol = Protocol.Ftp, installed = false, installType = ProtocolInstallType.IisRole,
                serviceStatus = ServiceState.Running, installCommand = "Add-WindowsCapability ...",
                installHint = "IIS 基础角色已安装，FTP 服务未运行",
            },
            [Protocol.Nfs] = new()
            {
                protocol = Protocol.Nfs, installed = false, installType = ProtocolInstallType.ServerFeature,
                serviceStatus = ServiceState.Unknown, installHint = "",
            },
        };
        await _vm.LoadAsync();
        var items = _vm.UninstalledBannerItems;
        Assert.Equal(2, items.Count);
        var ftp = items.First(i => i.Protocol == Protocol.Ftp);
        Assert.Contains(("IIS 已装", "green"), ftp.Badges);
        Assert.Contains(("服务已停", "volcano"), ftp.Badges);
        Assert.Contains("已装", string.Join(" ", ftp.Badges.Select(b => b.text)));
        Assert.Contains("FTP 角色未装", string.Join(" ", ftp.Badges.Select(b => b.text)));
        var nfs = items.Single(i => i.Protocol == Protocol.Nfs);
        Assert.Empty(nfs.Badges);
    }

    [Fact]
    public async Task Install_SucceedsAndReports()
    {
        MakeVm();
        _feed.Caps = new Dictionary<Protocol, ProtocolFeatureState>
        {
            [Protocol.Ftp] = new()
            {
                protocol = Protocol.Ftp, installed = false, installType = ProtocolInstallType.IisRole,
                installCommand = "cmd", installHint = "",
            },
        };
        await _vm.LoadAsync();
        var msg = await _vm.InstallProtocolAsync(Protocol.Ftp);
        Assert.Equal("", msg);
        Assert.Contains(Protocol.Ftp, _feed.Installed);
        Assert.Equal("安装成功，可能需要重启系统", _vm.StatusMsg);
    }

    // ===== 编辑保存（update 路由）=====

    [Fact]
    public async Task SaveEdit_RoutesToShareServiceForSmb()
    {
        MakeVm();
        await _vm.LoadAsync();
        var row = _vm.Rows.First(r => r.Share.name == "Docs");
        _vm.EditCommand.Execute(row);
        _vm.EditForm!.Description = "改过的";
        var msg = await _vm.SaveEditAsync();
        Assert.Equal("已保存", msg);
        var updated = _share.Updated.Single();
        Assert.Equal("Docs", updated.name);
        Assert.Equal("改过的", updated.opts.description);
    }

    // ===== 置顶持久化（appstate.json pinned）[Source: Shares.tsx#togglePin] =====

    [Fact]
    public async Task TogglePin_PersistsToAppstate()
    {
        var store = new AppSettingsStore(_stateDir);
        MakeVm();
        await _vm.LoadAsync();
        _vm.TogglePin("smb:Docs");
        Assert.Contains("smb:Docs", store.Load().pinned);
        _vm.TogglePin("smb:Docs");
        Assert.DoesNotContain("smb:Docs", store.Load().pinned);
    }

    // ===== 详情/权限子 VM 工厂 =====

    [Fact]
    public void MakeDetailVm_WiresServices()
    {
        MakeVm();
        var share = TestShares.Smb("Docs");
        var detail = _vm.MakeDetailVm(share);
        Assert.True(detail.IsSmb);
        var perm = _vm.MakePermissionVm(share);
        Assert.Same(share, perm.Share);
    }
}

