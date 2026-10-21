using WinShare.App.State;
using WinShare.Core.Contracts;

namespace WinShare.App.Tests;

/// <summary>告警评估器逐条对齐 Dashboard.tsx buildAttentionItems（四类 + 门控）。</summary>
public class AlertEvaluatorTests
{
    static AlertRules Rules(bool smb1 = true, bool weak = true, int lowGb = 20, int? idle = null) =>
        new()
        {
            idleAlertMinutes = idle,
            smb1Alert = smb1,
            weakPasswordAlert = weak,
            diskLowGb = lowGb,
        };

    [Fact]
    public void Smb1_Enabled_FiresDangerWhenRuleOn()
    {
        var items = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(),
            smbConfig = new SmbServerConfig { enableSMB1Protocol = true },
        });
        var smb1 = items.Single(i => i.key == "smb1");
        Assert.Equal("danger", smb1.level);
        Assert.Equal("/settings", smb1.route);
        Assert.Equal("smb1", smb1.attention);
    }

    [Fact]
    public void Smb1_DisabledOrRuleOff_NoItem()
    {
        Assert.Empty(AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(smb1: false),
            smbConfig = new SmbServerConfig { enableSMB1Protocol = true },
        }));
        Assert.Empty(AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(),
            smbConfig = new SmbServerConfig { enableSMB1Protocol = false },
        }));
    }

    [Fact]
    public void GuestFlags_FireItemWithLevelDependingOnInsecureLogons()
    {
        var warnOnly = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(),
            smbConfig = new SmbServerConfig { enableGuestUserAccess = true },
        });
        Assert.Equal("warn", warnOnly.Single(i => i.key == "guest").level);

        var danger = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(),
            smbConfig = new SmbServerConfig { enableInsecureGuestLogons = true },
        });
        Assert.Equal("danger", danger.Single(i => i.key == "guest").level);
    }

    [Fact]
    public void GuestFlags_SkippedWhenRuleOffOrConfigMissing()
    {
        Assert.Empty(AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(weak: false),
            smbConfig = new SmbServerConfig { enableGuestUserAccess = true },
        }));
        Assert.Empty(AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(),
            smbConfig = null, // 数据源不可用 → 如实跳过
        }));
    }

    [Fact]
    public void BlankAndNeverExpire_AggregateIntoPasswordItem()
    {
        var sec = new SecurityReport
        {
            Checked = 42,
            issues = new List<SecurityIssue>
            {
                new() { user = "u1", level = SecurityLevel.Fail, issue = "空口令" },
                new() { user = "u2", level = SecurityLevel.Warn, issue = "永不过期" },
                new() { user = "u3", level = SecurityLevel.Warn, issue = "弱口令" },
            },
        };
        var items = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput { alertRules = Rules(), security = sec });
        var pw = items.Single(i => i.key == "password");
        Assert.Equal("danger", pw.level); // 有 Fail
        Assert.Contains("1 个启用账户可空口令登录", pw.text);
        Assert.Contains("1 个账户密码永不过期", pw.text);
        Assert.Contains("共检查 42", pw.text);
        Assert.Equal("/users", pw.route);
    }

    [Fact]
    public void NeverExpireOnly_IsWarnLevel()
    {
        var sec = new SecurityReport
        {
            Checked = 5,
            issues = new List<SecurityIssue>
            {
                new() { user = "u", level = SecurityLevel.Warn, issue = "密码永不过期" },
            },
        };
        var pw = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput { alertRules = Rules(), security = sec })
            .Single(i => i.key == "password");
        Assert.Equal("warn", pw.level);
    }

    [Fact]
    public void DiskLow_FiresPerDiskWhenBelowThreshold()
    {
        var disks = new List<DiskUsage>
        {
            new() { drive = "E:", freeGB = 5, totalGB = 100, freePct = 5 },  // 5 < 20/4=5? 5<=5 → danger
            new() { drive = "F:", freeGB = 15, totalGB = 100, freePct = 15 }, // 15 < 20 → warn
            new() { drive = "C:", freeGB = 50, totalGB = 100, freePct = 50 }, // 正常
        };
        var items = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput { alertRules = Rules(), disks = disks });
        var e = items.Single(i => i.key == "disk:E:");
        var f = items.Single(i => i.key == "disk:F:");
        Assert.Equal("danger", e.level);
        Assert.Equal("warn", f.level);
        Assert.DoesNotContain(items, i => i.key == "disk:C:");
    }

    [Fact]
    public void DiskLow_DisabledWhenThresholdZero()
    {
        var disks = new List<DiskUsage> { new() { drive = "E:", freeGB = 1, totalGB = 100, freePct = 1 } };
        Assert.Empty(AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(lowGb: 0),
            disks = disks,
        }));
    }

    [Fact]
    public void IdleAlertMinutes_IsIntentionallyIgnored()
    {
        // idleAlertMinutes 无会话空闲数据 → 不臆造告警（对齐 TS 注释语义）
        var items = AlertEvaluator.Evaluate(new AlertEvaluator.AttentionInput
        {
            alertRules = Rules(idle: 5),
            smbConfig = new SmbServerConfig(),
        });
        Assert.Empty(items);
    }

    [Fact]
    public void DiskLevel_MatchesShareDefaultsSemantics()
    {
        Assert.Equal("danger", AlertEvaluator.DiskLevel(new DiskUsage { freePct = 4, freeGB = 100 }));      // 4 <= 10/2
        Assert.Equal("danger", AlertEvaluator.DiskLevel(new DiskUsage { freePct = 90, freeGB = 4 }, 10, 20)); // 4 <= 20/4
        Assert.Equal("warn", AlertEvaluator.DiskLevel(new DiskUsage { freePct = 9, freeGB = 100 }));       // 9 <= 10
        Assert.Equal("warn", AlertEvaluator.DiskLevel(new DiskUsage { freePct = 60, freeGB = 19 }, 10, 20)); // 19 <= 20
        Assert.Equal("ok", AlertEvaluator.DiskLevel(new DiskUsage { freePct = 60, freeGB = 100 }));
        Assert.Equal("unknown", AlertEvaluator.DiskLevel(null));
    }
}
