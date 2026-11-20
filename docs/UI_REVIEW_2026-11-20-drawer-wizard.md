# WinShare Panel 抽屉/向导浅色专项评审（2026-11-20）

接续 2026-11-19 全量评审（docs/UI_REVIEW_2026-11-19.md），本轮聚焦**抽屉/向导/弹窗窗口的浅色主题逐窗屏幕取证 + 键盘可达性 + DPI**。

## 0. 结论摘要

- **逐窗取证**：12 个窗口 × 各状态 = 19 张浅色截图，存 `.repro/ui-shots/lt/`（gitignore，本地留存）。
- **全程非激活**：新增 `WSHARE_NOACTIVATE=1` 取证钩子（`UiTestHooks`：`ShowActivated=false` + `WS_EX_NOACTIVATE`），app 启动与逐窗截图均不抢用户前台（实测 t=0~12s 前台始终为用户窗口；UIA 操作触发的瞬时激活由脚本侧还原）。
- **修复 3 项 P1**：IPC$ 管理共享泄漏进业务列表（连带首启向导永不弹）、主按钮渐变白字对比度 1.35:1、未勾选复选框边框不可见。修复后逐窗复验截图确认。
- **推迟 2 项实测**：键盘 Tab 走查、DPI 动态缩放复截——执行期间用户活跃（GetLastInputInfo 空闲 <10min），按「不抢前台」约定未注入按键/未改系统缩放；静态审计结论见 §2/§3，复跑方式见 §5。
- **实测补充（当日稍晚空闲窗口）**：主窗口 Tab 走查已实测完成（30 步序列 + 2 张焦点截图，见 §2 G-K4）；详情抽屉/向导 Tab 走查与 DPI 150% 动态复截仍为推迟——门控在用户返回时正确中止。

## 1. 修复记录（本轮，全部已修并复验）

### F1（P1）IPC$ 管理共享泄漏进业务列表

- **证据**：`lt-edit-share.png`——共享列表把 IPC$ 当普通共享展示，且可打开「编辑共享：IPC$」窗口（名称/路径/描述均可改）。
- **连带**：首启向导判定 `shares.Count == 0` 因 IPC$ 占位永不成立——**装了本应用的机器永远看不到首启三问向导**（`lt-firun-*` 为修复后强制弹出取证）。
- **修复**：`ShareService.ListSharesAsync` 过滤 `ShareType.Special/IPC`，对齐本文件快照序列化处既有排除语义；`Get-SmbShare` 的 IPC$（Special=true）不再进业务列表。
- **复验**：空共享环境下首启向导正常弹出（`lt-firun-step1/2/3.png`）。

### F2（P1）主按钮渐变与白字对比 1.35:1

- **证据**：`lt-wizard-smb.png`「创建」、`lt-user-create.png`「创建」、`lt-edit-share.png`「保存」、`lt-firun-step3.png`「创建共享」——浅色 `BrushBrandGradient`（#A8DCF5→#C8B6E2→#F5C6C6）+ 白字，文字几乎不可辨。
- **修复**：浅色渐变加深至 #2E9BDB/#8B76C2/#C97979，全渐变段对白字 ≥3:1（色相不变，对齐深色主题饱和度）。
- **复验**：`lt-fix-wizard.png` 白字清晰可辨。

### F3（P1）未勾选复选框边框不可见

- **证据**：`lt-wizard-smb.png`「启用 SMB 加密」只剩一行空白；`lt-wizard-nfs.png` 认证方式 Krb5/Krb5i/Krb5p/AUTH_SYS 四个方框全部消失；`lt-firun-step3.png`「高级参数」同。
- **根因**：`CheckBoxSoft` 未勾选态边框用 `BrushBorder`（浅色 #E3EEF6），与卡底 #F4FAFD 对比 ≈1.05:1。
- **修复**：新增专用 token `BrushCheckBoxBorder`（Light #A8C8DE / Dark #44566A，两主题同名），`CheckBoxSoft` 模板改用之；全应用 CheckBox（向导/用户对话框/权限抽屉）一并受益。
- **复验**：`lt-fix-wizard.png` 方框描边可辨。

### F4（P1，随 F1 解决）首启向导判定占位失效

见 F1 连带项；无需额外改动。

## 2. 键盘可达性（静态审计；实测推迟）

**已达标的**：

- 向导（新建共享）：Esc 关闭（PreviewKeyDown，非模态下 IsCancel 不生效已处理）、取消 IsCancel ✓。
- 用户/组对话框（BatchAssign/GroupCreate/UserCreate/UserEdit）：取消 IsCancel、主键 IsDefault 全覆盖 ✓。
- 页面级快捷键：共享页 Ctrl+N/Del/Space（对齐 TS hotkey）✓。

**发现的缺口**（G-K1~K4 已于 2026-11-20 第二批全部修复）：

- **G-K1（P1）详情/权限抽屉无键盘关闭路径** ✅ 已修：页面 PreviewKeyDown 加 Esc 分支——权限抽屉沿用遮罩关闭的未保存确认路径，不绕过确认。
- **G-K2（P2）ShareEditWindow 无 Esc/Enter** ✅ 已修：Esc（PreviewKeyDown，非模态窗 IsCancel 不生效）+ 保存 IsDefault + 取消 IsCancel。
- **G-K3（P2）首启向导无 Esc/IsDefault** ✅ 已修：Esc 关闭=放弃本次（不写完成标记，下次仍会弹）；非末步「下一步」IsDefault=Enter 前进，**末步不设默认——防 Enter 误触真实建共享**。
- **G-K4（P2）自定义标题栏窗口按钮（—/□/?）参与 Tab 序列** ✅ 已修：三按钮 Focusable=False（鼠标点击不受影响）。主窗 Tab 走查实测（空闲门控通过后执行）：侧栏 → 页面控件 → 顶栏 → 窗口装饰按钮 → 循环回侧栏，Tab 环闭合 ✓；焦点态截图 `lt-focus-main-f5/f15.png`。另：搜索框补 `AutomationProperties.Name`（占位水印不是自动化名，读屏不可述）。
> 三项均为小改动（1-2 行/处），建议下一批键盘专项统一修（本轮控制变更面）。

## 3. DPI

- **静态**：`app.manifest` PerMonitorV2 ✓；`UseLayoutRounding` 全局开启 ✓；对话框均为 `SizeToContent`/`MaxHeight` 自适应 ✓。
- **风险点**：**D1（P2）** `ShareDetailDrawer.xaml:124/162` 打开文件/连接两个 DataGrid `Height="320"` 固定像素——150% 缩放下可视行数减半且不随内容收缩，建议改 `MaxHeight`。
- **动态复截（150%/125% 缩放逐窗复截）**：**推迟**——需临时改系统缩放（WindowMetrics.AppliedDPI + 广播），执行期间用户活跃未执行。

## 4. 逐窗取证清单（浅色，`.repro/ui-shots/lt/`）

| 窗口 | 截图 | 状态 |
| --- | --- | --- |
| 首启三问向导 | lt-firun-step1/2/3.png | 逐步（修复 F1 后强制弹出） |
| 新建共享向导 | lt-wizard-smb / -nfs / -bottom.png | SMB 态 / NFS 态 / 滚动底部 |
| 详情抽屉（内嵌） | lt-detail-top / -bottom.png | 右侧宿主 + 滚动 |
| 权限抽屉（内嵌） | lt-perm-top / -bottom.png | 共享权限 + NTFS ACL 区 |
| 编辑共享窗 | lt-edit-share / -bottom.png | 非模态 Drawer 窗 |
| 新建本地用户 | lt-user-create / -bottom.png | 上轮 footer 修复复验 ✓ |
| 用户详情抽屉 | lt-user-edit / -bottom.png | 非模态 |
| 组管理抽屉 | lt-group-manage / -bottom.png | 非模态 |
| 新建本地组 | lt-group-create.png | 模态转非激活 |
| 批量分配到组 | lt-batch-assign.png | — |
| 修复复验 | lt-fix-wizard / lt-fix-usercreate.png | F2/F3 修复后 |

## 5. 推迟项复跑方式

键盘 Tab 走查与 DPI 动态复截脚本已就绪（`.repro/ui-shots/dw-capture.ps1` 内含 idle 门控：`GetLastInputInfo` 空闲 ≥10min 才注入按键/改缩放，任何状态改动结束即还原）。用户空闲时段（或下班挂机）直接重跑该脚本即可补齐 `lt-focus-*` 与 `lt-dpi150-*` 两组证据。
