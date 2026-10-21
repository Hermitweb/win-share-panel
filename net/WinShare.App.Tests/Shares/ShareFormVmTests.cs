using WinShare.App.Shares;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests.Shares;

/// <summary>
/// 新建/编辑表单 VM 测试（对齐 Shares.tsx 新建 Modal 的 fillPathAndName/新手模式收敛/
/// 场景预设/创建输入组装；编辑 Modal 的 update 输入组装）。
/// </summary>
public class ShareFormVmTests
{
    // ===== fillPathAndName [Source: Shares.tsx#fillPathAndName] =====

    [Fact]
    public void FillPathAndName_UntouchedNameGetsAutoSuggest()
    {
        var form = new ShareCreateFormVm();
        form.FillPathAndName(@"E:\SharedDocs");
        Assert.Equal(@"E:\SharedDocs", form.Path);
        Assert.Equal("SharedDocs", form.Name);
    }

    [Fact]
    public void FillPathAndName_TouchedNameIsNeverOverwritten()
    {
        var form = new ShareCreateFormVm { Name = "Mine", NameTouched = true };
        form.FillPathAndName(@"E:\Other");
        Assert.Equal(@"E:\Other", form.Path);
        Assert.Equal("Mine", form.Name); // 自动值不得覆盖用户输入
    }

    [Fact]
    public void FillPathAndName_DriveRootKeepsUserInput()
    {
        var form = new ShareCreateFormVm();
        form.FillPathAndName(@"E:");
        Assert.Equal("", form.Name); // 盘符根不带名（TS shareNameFromPath 返回空）
    }

    [Fact]
    public void OnPathText_KeepsNameWhenUntouched()
    {
        var form = new ShareCreateFormVm();
        form.OnPathText(@"D:\Keep");
        Assert.Equal("Keep", form.Name);
        form.NameTouched = true;
        form.OnPathText(@"D:\Another");
        Assert.Equal("Keep", form.Name);
    }

    // ===== 新手模式高级项收敛 [Source: Shares.tsx#handleCreate ADVANCED_FORM_KEYS] =====

    [Fact]
    public void BuildCreateInput_BeginsModeStripsAdvancedFields()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "smb", Name = "n", Path = @"E:\n",
            Encrypted = true, EncryptData = true, ConcurrentUserLimit = 5,
            CachingMode = "BranchCache", FolderEnumerationMode = "AccessBased", ShareShadowCopy = true,
        };
        var input = form.BuildCreateInput(advancedMode: false);
        Assert.Null(input.encrypted);
        Assert.Null(input.encryptData);
        Assert.Null(input.concurrentUserLimit);
        Assert.Null(input.cachingMode);
        Assert.Null(input.folderEnumerationMode);
        Assert.Null(input.shareShadowCopy);
    }

    [Fact]
    public void BuildCreateInput_AdvancedModeSubmitsFields()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "smb", Name = "n", Path = @"E:\n",
            Encrypted = true, EncryptData = true, ConcurrentUserLimit = 5,
            CachingMode = "BranchCache", FolderEnumerationMode = "AccessBased", ShareShadowCopy = true,
        };
        var input = form.BuildCreateInput(advancedMode: true);
        Assert.True(input.encrypted);
        Assert.True(input.encryptData);
        Assert.Equal(5, input.concurrentUserLimit);
        Assert.Equal(CachingMode.BranchCache, input.cachingMode);
        Assert.Equal(FolderEnumerationMode.AccessBased, input.folderEnumerationMode);
        Assert.True(input.shareShadowCopy);
    }

    [Fact]
    public void BuildCreateInput_ReadAccessDefaultsToUsersWhenEmpty()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "smb", Name = "n", Path = @"E:\n",
            // 访问控制未展开时 readAccess 保持初始默认（Users）
        };
        var input = form.BuildCreateInput(advancedMode: false);
        Assert.Equal(new[] { "Users" }, input.readAccess);
    }

    [Fact]
    public void BuildCreateInput_ClearingReadAccessYieldsNull()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "smb", Name = "n", Path = @"E:\n", ReadAccess = "",
        };
        var input = form.BuildCreateInput(advancedMode: false);
        Assert.Null(input.readAccess); // 空=未填，走服务层默认（TS undefined 等价）
    }

    [Fact]
    public void BuildCreateInput_NfsFields()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "nfs", Name = "n", Path = @"E:\n",
            Authentication = "krb5i,sys", NfsPermission = "ro",
            AllowRootAccess = false, EnableUnmappedAccess = false,
        };
        var input = form.BuildCreateInput(advancedMode: true);
        Assert.Equal(Protocol.Nfs, input.protocol);
        Assert.Equal(new[] { NfsAuthMethod.Krb5i, NfsAuthMethod.Sys }, input.authentication);
        Assert.Equal(NfsPermission.Ro, input.nfsPermission);
    }

    [Fact]
    public void BuildCreateInput_FtpFields()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "ftp", Name = "n", Path = @"E:\n",
            Port = 2121, SslPolicy = "SslRequire", AuthMode = "windows",
        };
        var input = form.BuildCreateInput(advancedMode: false);
        Assert.Equal(Protocol.Ftp, input.protocol);
        Assert.Equal(2121, input.port);
        Assert.Equal(SslPolicy.SslRequire, input.sslPolicy);
        Assert.Equal(FtpAuthMode.Windows, input.authMode);
        Assert.Null(input.authentication); // 非 NFS
    }

    [Fact]
    public void BuildCreateInput_WebdavAnonymous()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "webdav", Name = "n", Path = @"E:\n", AnonymousEnabled = true,
        };
        var input = form.BuildCreateInput(advancedMode: false);
        Assert.Equal(Protocol.Webdav, input.protocol);
        Assert.True(input.anonymousEnabled);
    }

    [Fact]
    public void BuildSmbCreateOpts_AdvancedGating()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "smb", Name = "n", Path = @"E:\n",
            Encrypted = true, ConcurrentUserLimit = 0, CachingMode = "Manual",
        };
        var novice = form.BuildSmbCreateOpts(advancedMode: false);
        Assert.Null(novice.encrypted);
        Assert.Null(novice.concurrentUserLimit); // 0 = 无限制不提交
        var expert = form.BuildSmbCreateOpts(advancedMode: true);
        Assert.True(expert.encrypted);
        Assert.Equal(CachingMode.Manual, expert.cachingMode);
    }

    // ===== 场景预设 [Source: Shares.tsx#CREATE_SCENARIO_PRESETS + presetUsesAdvanced] =====

    [Fact]
    public void Scenarios_NoviceModeHidesAdvancedPresets()
    {
        var novice = CreateScenarioPresets.ForProtocol("smb", advancedMode: false);
        // 「私有加密」写 encrypted/encryptData/AccessBased（高级键）→ 新手模式收敛
        Assert.DoesNotContain(novice, p => p.Name == "私有加密");
        Assert.Contains(novice, p => p.Name == "团队协作");
    }

    [Fact]
    public void Scenarios_ExpertModeShowsAllSmbPresets()
    {
        var expert = CreateScenarioPresets.ForProtocol("smb", advancedMode: true);
        Assert.Equal(3, expert.Count);
        Assert.Contains(expert, p => p.Name == "私有加密");
    }

    [Fact]
    public void ApplyScenario_FillsValuesAndStaysAdjustable()
    {
        var form = new ShareCreateFormVm { Protocol = "smb" };
        var preset = CreateScenarioPresets.ForProtocol("smb", true).First(p => p.Name == "团队协作");
        form.ApplyScenario(preset);
        Assert.Equal("Administrators", form.FullAccess);
        Assert.Equal("Users", form.ChangeAccess);
        // 填入后仍可逐项调整
        form.FullAccess = "Administrator";
        Assert.Equal("Administrator", form.FullAccess);
    }

    [Fact]
    public void ApplyScenario_PrivateEncryptionSetsEncryptionFields()
    {
        var form = new ShareCreateFormVm { Protocol = "smb" };
        var preset = CreateScenarioPresets.ForProtocol("smb", true).First(p => p.Name == "私有加密");
        form.ApplyScenario(preset);
        Assert.True(form.Encrypted);
        Assert.True(form.EncryptData);
        Assert.Equal("AccessBased", form.FolderEnumerationMode);
        Assert.Empty(form.ChangeAccess);
    }

    // ===== 校验（antd rules） =====

    [Fact]
    public void Validate_RequiresNameAndPath()
    {
        var form = new ShareCreateFormVm { Path = @"E:\x" };
        Assert.False(form.Validate(out var err));
        Assert.Equal("请输入共享名", err);
        form.Name = "n";
        form.Path = "";
        Assert.False(form.Validate(out err));
        Assert.Equal("请输入或拖入路径", err);
        form.Path = @"E:\x";
        Assert.True(form.Validate(out _));
    }

    // ===== 创建后复位（TS form.resetFields() + nameTouchedRef=false） =====

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var form = new ShareCreateFormVm
        {
            Protocol = "ftp", Name = "x", Path = @"E:\x", NameTouched = true,
            Port = 22, SslPolicy = "SslRequire",
        };
        form.Reset();
        Assert.Equal("smb", form.Protocol);
        Assert.Equal("Users", form.ReadAccess); // 初始默认 DEFAULT_READ_ACCESS
        Assert.False(form.NameTouched);
        Assert.Equal(21, form.Port);
        Assert.Equal("SslAllow", form.SslPolicy);
        Assert.Equal("rw", form.NfsPermission);
    }

    // ===== 编辑表单输入组装 [Source: Shares.tsx#handleEdit] =====

    [Fact]
    public void EditForm_NfsAdvancedGating()
    {
        var form = new ShareEditFormVm
        {
            Name = "n", Path = @"E:\n", Protocol = Protocol.Nfs,
            NfsPermission = "ro", AllowRootAccess = true,
        };
        var novice = form.BuildUpdateInput(advancedMode: false);
        Assert.Null(novice.allowRootAccess); // 新手模式不提交 root 访问
        Assert.Equal(NfsPermission.Ro, novice.nfsPermission);
        var expert = form.BuildUpdateInput(advancedMode: true);
        Assert.True(expert.allowRootAccess);
    }

    [Fact]
    public void EditForm_FtpOnlySendsFtpFields()
    {
        var form = new ShareEditFormVm
        {
            Name = "n", Protocol = Protocol.Ftp, SslPolicy = "SslRequireCredentials", AuthMode = "anonymous",
        };
        var input = form.BuildUpdateInput(advancedMode: true);
        Assert.Equal(SslPolicy.SslRequireCredentials, input.sslPolicy);
        Assert.Equal(FtpAuthMode.Anonymous, input.authMode);
        Assert.Null(input.nfsPermission);
        Assert.Null(input.anonymousEnabled);
    }
}
