# 审计汇总与优先级缺陷 Backlog

- 汇总自：01-main-process.md、02-renderer.md、03-security.md、04-engineering.md
- 汇总人：Captain（audit-* 成员因上游模型配额耗尽，由 Captain 接管复核与汇总）
- 日期：2026-06

## 1. 总体风险评估

**整体风险：中低。** 安全核心（PowerShell 命令注入防线）经静态分析 + 191 既有测试实证为**健壮**，审计员中断前标注的"关键注入"复核为**误报**。工程化基线良好（typecheck/test 全绿），主要短板在**质量门禁缺失**（lint/CI/覆盖率）与**本地-CI 工具链版本漂移**。

## 2. 修复顺序与依赖

```
阶段1（阻塞解除，已完成）：E1 pnpm 工具链修复 ✅
阶段2（质量门禁，可并行）：E2 ESLint+Prettier  →  E3 CI 门禁（依赖 E2 的 lint 脚本）
阶段3（测试基建）：E4 coverage + src 首批单测
阶段4（规范与小优化）：R-2 清噪音 / E-8 gitignore / E-10 dev 脚本 / E-6 test typecheck
阶段5（可选强化）：M-1 nfs 字段 / E-2 工具链对齐 / R-1 导入护栏 / R-5 组件抽象
```

依赖说明：
- E2 必须先于 E3（CI 引用 lint 脚本）。
- E2 大 diff 会格式化 src/electron，应在其他源码改动（M-*/R-*）之前或同步处理，避免冲突。
- E-6（test 纳入 typecheck）建议在 E2 之后做，因 lint/格式化会改变文件。

## 3. 优先级缺陷清单（去重合并）

| 优先级 | ID | 标题 | 域 | 自动验证 | 建议执行者 |
|---|---|---|---|---|---|
| P0 | （无阻断性漏洞） | — | — | — | — |
| P1 | E-1 | pnpm 工具链修复 | 工程化 | 可（已修） | ✅ 已完成 |
| P1 | E-2 | CI 与本地工具链版本对齐（pnpm9↔11、Node22↔24） | 工程化 | 部分 | Captain |
| P1 | E-3 | 引入 ESLint + Prettier，全库 lint 归零 | 工程化 | 可 | Captain（E2 任务） |
| P1 | E-4 | CI 增加 test/lint 门禁 | 工程化 | 可 | Captain（E3 任务） |
| P2 | E-5 | coverage 配置 + src 首批单测 | 工程化 | 可 | Captain（E4 任务） |
| P2 | E-6 | 测试纳入 typecheck（tsconfig.test.json） | 工程化 | 可 | Captain |
| P2 | M-1 | nfs.setConfig 静默丢弃 gatewayCharacterSet/protocolVersion | 主进程 | 可 | Captain |
| P2 | R-1 | 导入 JSON 无大小/深度护栏 | 渲染层 | 可 | Captain |
| P3 | M-2/M-3/M-4/M-5/M-7 | 进程池杂散标记/RollbackRestore 注释/Math.random token/可读性等 | 主进程 | 部分 | Captain（可选） |
| P3 | R-2 | 删除 `void config` 噪音 | 渲染层 | 可 | Captain |
| P3 | R-3/R-4/R-5 | hook 豁免/重复 detect/组件抽象 | 渲染层 | 部分 | Captain（可选） |
| P3 | R-6 | api.ts 透传 AppError.code/category | 渲染层 | 可 | Captain |
| P3 | R-7 | 死样式清理 | 渲染层 | 部分 | Captain |
| P3 | E-7 | tsconfig 收紧 noUnused* | 工程化 | 可 | Captain（E2 内） |
| P3 | E-8 | .gitignore 忽略 .agent-teams/docs/audit | 工程化 | 可 | Captain |
| P3 | E-9 | 删除空 scripts/ 或填充工具脚本 | 工程化 | 可 | Captain |
| P3 | E-10 | dev 脚本单 & 改 && | 工程化 | 可 | Captain |
| P3 | E-11 | release.yml 改用 --frozen-lockfile | 工程化 | 可 | Captain（随 E-2） |

## 3.5 执行状态（2026-06 收尾标注）

已闭环：E-1（t5）、E-2 工具链对齐（packageManager 单一真相源，t9/E3 顺带）、E-3（t7）、E-4（t9）、E-5（t8）、**E-6/E-7/M-1/R-1/R-2/E-8/E-9/E-10/E-11（t16 集成任务）**；质量门 R1(t10)=pass、R2(t11)→修复(t14)→round2(t15)=pass、R4(t12)=pass、R3(t13)=pass。终态六门禁全部 exit 0（typecheck/lint/format:check/test 220 用例/test:coverage/build）。

仍为开放项（P3，建议后续专项）：M-2~M-5、M-7（注释/可读性级）；R-3~R-5、R-6、R-7（组件抽象/错误分类透传/死样式）；R-8（react-hooks set-state-in-effect 数据获取模式重构，见 eslint.config.mjs 留档）；F-1/F-3/F-4（IPC 逐字段校验、提权路径单测、渲染层入参守卫）。组件级 jsdom 测试基建亦未引入（见 t8 取舍记录）。

### P3 第二批（2026-06 追加，Captain 直改 + 六门禁复验）

已闭环：**M-2/M-3/M-4/M-6**（快照链注释、池空闲杂散输出丢弃+回归测试、crypto token、恢复报错场景化）、**R-3/R-4 + R-5(部分)**（useEnsureProtocolCaps 统一 Dashboard/Banner/三面板五处探测）、**R-6**（api.call 透传 code/category + 2 用例）、**R-7**（死样式清理：tailwind 5 项 + CSS 变量 6 项，逐项引用扫描取证，build 复验）、**F-1/F-4**（IPC 逐字段形状守卫，仅拒畸形不改合法语义）、**F-3**（含空格路径 psQuote 单测）；M-5/M-7 原判即"维持现状"，随批次关闭。

开放项收敛为三项专项：**R-8** 数据获取模式重构（26 处挂载 fetch → defer/React Query，完成后复启 set-state-in-effect）；**R-5 全量** 三 PermPanel/三 SettingsPanel 表单级抽象；**jsdom 组件测试基建**（t8 取舍留档）。

### 渲染层批 2（renderer-debt，收口标注）

已闭环：**R-5 全量组件抽象**（三 PermPanel + 三 SettingsPanel → `useProtocolPermissions` / `useProtocolSettings` 两个 hook + `ProtocolPermissionShell` / `ProtocolSettingsShell` 两个 Shell；六个面板内 `useEffect|useQuery|invalidateQueries|queryKey|setLoading|setRows|setSaving` 命中 0、9 个 IPC 调用点收敛为 1 份、Shell 文案 3→1、六面板合计净减 69 行；新增 59 例面板用例）；**`react-hooks/set-state-in-effect` 14 处违例全量清零并把规则复启为 error**（`eslint.config.mjs`；起点实测 **14 处**，旧留档的「10 处」只统计 B1 的 26→10 收敛、漏计批 1 新增的 4 处）。

R-3 状态如实：`src/hooks/useTickEffect.ts:13` 的 `react-hooks/exhaustive-deps` 行内豁免**保持原样**——P3 第二批已按审计建议判为「可接受」（注释说明意图为「跳过首次挂载」）；`exhaustive-deps` 规则仍开启，仅该处豁免且附理由。本批次未改动该文件，也未改写为 `useLatest` 形态。

R-8 仍为开放项（按事实拆分，不笼统记）。其**阻塞条件**「复启 set-state-in-effect」已随本批次完成（规则现为 `error`、全库零违例、该规则 suppressions=0）；但 R-8 作为「数据获取模式重构」条目本身**未闭环**——本批次明确不改数据获取范式（属非目标，且规格 §4.1 禁止引入缓存层），挂载取数并未全部改写为 defer/React Query。

其它未完成/已知边界（详见 CHANGELOG「未完成 / 已知边界」）：jsdom 组件测试基建仍未引入；`electron/lib/powershellPool.stress.test.ts`（「场景2：100 并发含 10 条卡死命令」）的既有负载敏感**断言**抖动未根治（本批次零改动，建议另开专项）；本批次证据只到 jsdom + 门禁级（IPC 全为 mock），无真机端到端。

质量门（本批次）：独立验证 round 1 **红**（F1 行为回归 + F2 门禁不绿）→ round 2 **红**（F1 已闭环、F2/F3 未闭环）→ round 3 **绿**；此后新发现的 F4 由 t13 修复、复验绿；评审 t5（R-5 抽象）/ t6（modal/drawer 迁移）均 **verdict = pass**、无 findings。终态六门禁逐条 exit 0（typecheck / lint / format:check / test 47 文件 650 用例 / test:coverage / build）。规格自身的 6 处写错与 1 处自相矛盾见 `05-renderer-debt.md` §7.3。

## 4. 执行摘要（给决策者的话）

审计未发现任何可被利用的安全漏洞；代码分层、事务补偿、生命周期管理、注入防护均达到生产级。真正需要"工程化/规范化"的是：把本地已通过的 typecheck/test 固化为 **CI 门禁**（ESLint/Prettier + 测试 + 覆盖率），并解决 **本地 pnpm9/Node24 与 CI pnpm11/Node22 的版本漂移**（否则 CI 可能用不同工具链重建 lockfile，破坏可重现构建）。这两项落地后，项目即达到"提交即门禁、可重现发布"的工程化标准。
