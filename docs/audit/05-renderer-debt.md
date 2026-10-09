# 05 · 渲染层债清零（批 2）施工规格

> 批次目标：为 Shares 页下一次 UI 改动清出干净底座——14 处 `react-hooks/set-state-in-effect` 违例全量迁移并复启规则为 error；R-5 全量组件抽象（三 PermPanel + 三 SettingsPanel）；行为零变化的独立验证与评审。
>
> 本文件是**唯一施工规格**（single source of truth）。t2（panels-dev）、t3（modals-dev）按本文实现；t4（verifier）按本文回归矩阵与 §0.4 的规则行为做独立验证；t5/t6（reviewer）按本文逐条判定；t7（integrator）按 §0.3 勘误后的命令复启规则。
>
> 本文只写文档，未改动任何 `src/**`、`electron/**` 代码（证据见 §6）。
>
> 归属：analyst（requirements）· 轮次 1 · 依赖：无

---

## 0. 口径、基线与两条前置勘误

### 0.1 范围与非目标

**做**：14 处违例的逐点迁移方案；R-5 三 PermPanel + 三 SettingsPanel 的共享抽象对外 API；硬性边界与文件白名单；回归矩阵（逐点映射既有/新增/新建测试 + 断言行为）。

**不做（本批次禁止夹带）**：暗色模式双轨接线、英文 i18n、应用内检查更新、设置整体导入导出、预约式任务、真机端到端（真实 SMB/IIS/防火墙/icacls/开机自启/跨小时趋势）。任何一项都不许出现在本批次的 diff 里。

### 0.2 起点基线（实测，非引用留档）

| 项 | 实测值 | 取证 |
| --- | --- | --- |
| typecheck | exit 0 | `pnpm typecheck`（node/web 两套） |
| lint（仓库配置，规则仍为 off） | exit 0 | `pnpm lint` |
| vitest | 36 文件 / 520 用例全通过 | `pnpm test` |
| **set-state-in-effect 违例** | **14 条** | 见 §0.3 |
| git 工作区 | 干净（`git status --short` 空输出） | 见 §6 |
| 工具链 | `node_modules/.bin`、pnpm 9.12.0、`eslint-plugin-react-hooks@7.1.1`、`@tanstack/react-query@5.104.1` 可用 | — |

### 0.3 勘误 A：本批次 verify 命令里的 `--format unix` 在本机不可用（须由 captain 改任务契约）

契约给的 verify 命令：

```
node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}' --format unix
```

本机实测**必然失败**，与代码状态无关：

```
node.exe : The unix formatter is no longer part of core ESLint. Install it manually with
`npm install -D eslint-formatter-unix`
   [exit code: 2]
```

`node_modules/eslint-formatter-unix` 不存在，且 `package.json` 在本批次所有任务（t1/t2/t3/t4/t7）的 outOfScope 内，**不能在批次内安装它**。

等价可用命令（同规则、同文件集、同判据）：

```powershell
# 人读：默认 stylish，违例逐条给 file:line:col
node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}'
# 机读：JSON，可脚本化核对 file/line/column/ruleId
node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}' --format json
```

- 起点：上面两条均 **exit 1，恰好 14 条 `react-hooks/set-state-in-effect` error**。
- 终点（本批次完成后）：**exit 0**。
- **影响面（已核实）**：本批次只有 **t1 与 t4** 的 verify 含 `--format unix`；t2/t3 的 verify 不含该参数；**t7 的 verify 一直是纯 pnpm 门禁**（typecheck / lint / format:check / test / test:coverage / build），从来不含它。captain 已把 t4 的 verify 改为 `--format json`（本规格 §5.3 的取证同步改用 `--format json`，等价且可机读）。

### 0.4 勘误 B：规则行为实测——它决定了本批次「怎么改才算对」

`react-hooks@7.1.1` 的 `set-state-in-effect` 是编译器风格规则，**只报它认为同步发生的 setState**。动手前用一次性探针文件（仓库根目录，测完即删，事后 `git status --short` 为空）实测出 7 条行为，它们是本规格所有方案选择的依据：

| 编号 | 实测结论 | 探针证据 |
| --- | --- | --- |
| **P-1** | **同一 effect 内多处同步 setState 只报第一处**（`setA(1); setB(2)` 只报 `setA` 那行） | 探针 A：报 9:5，不报 10:5 |
| **P-2** | 同一组件内**两个** effect 各自报一次 | 探针 K1：报 13:5 与 17:5 |
| **P-3** | 调用「**组件体内定义**、内部含 setState」的函数一律按同步 setState 报（即使其 setState 在 `await` 之后）；**定义在 effect 回调内部**的同类函数则不报 | 探针 J3 报 50:10（组件体 loader）；J1/J2/J4/J5 零报（effect 内 loader、`.then` 回调、async IIFE） |
| **P-4** | 条件由 **ref** 决定时该 setState **不报**；条件由 prop/state 决定时**仍报** | 探针 K2 零报（`if (!edited.current) setName(path)`，正是 `FirstRunGuide:75` 未进清单的原因）；探针 K3 报 46:5 |
| **P-5** | **渲染期调整 state**（含自定义 hook 内的 reset 回调）**不报** | 探针 C（组件内 prev-value 模式）、探针 D（hook 内调 reset 回调）均零报 |
| **P-6** | `useLayoutEffect` 里的同步 setState **同样被报** | 探针 H：报 78:5 |
| **P-7** | 由普通布尔分支守卫的 setState **仍被报** | 探针 I：报 88:18 |

**由 P-1 推出的实现纪律（t2/t3 必须遵守）**：报告行只是该 effect 的**第一处**同步 setState。**修完报告行后必须重跑 lint**——同 effect 内后续 setState（以及 P-3 意义上的组件体载入函数调用）会浮现为新违例。因此 §1.3 逐点列出了**整个 effect 的全部同步 setState 清单**，实现者必须整段处理，而不是只删报告的那一行。

**由 P-3/P-4/P-6 推出的禁止清单（规避手法红线）**：

1. 禁止把同步 setState 藏进 `setTimeout` / `queueMicrotask` / `requestAnimationFrame` / `Promise.resolve().then` 以静音规则。
2. 禁止把 `useEffect` 换成 `useLayoutEffect` 迁移同一段同步 setState（P-6：照样报）。
3. 禁止为了吃到 P-4 的"ref 守卫不报"而把本应参与渲染的 state 搬进 ref。
4. 禁止用 `eslint-disable-next-line react-hooks/set-state-in-effect` 行内静音——本批次要求**每个被改文件该规则零违例、零 disable**（`exhaustive-deps` 的 disable 带理由仍按仓库既有风格允许，见 §2.1）。
5. 禁止改 `eslint.config.mjs` 把规则退回 off（那是 t7 的复启动作，方向相反）。

---

## 1. 14 处违例：清单、分类与选定方案

### 1.1 实测清单（与本机 lint 输出逐字一致）

命令 `node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}'` → exit 1，**14 条**，全部为 `react-hooks/set-state-in-effect`：

| # | 文件:行:列 | 类 | 归属 |
| --- | --- | --- | --- |
| 1 | `src/components/DiagnoseModal.tsx:158:5` | B+A | t3 |
| 2 | `src/components/FirstRunGuide.tsx:52:5` | A+B | t3 |
| 3 | `src/components/GroupManageModal.tsx:63:7` | A+B | t3 |
| 4 | `src/components/JournalDrawer.tsx:127:5` | B | t3 |
| 5 | `src/components/PermissionDrawer.tsx:118:7` | B+A | t3 |
| 6 | `src/components/PermissionMatrix.tsx:295:5` | B | t3 |
| 7 | `src/components/PermissionPanel/FtpPermPanel.tsx:87:5` | B | t2 |
| 8 | `src/components/PermissionPanel/NfsPermPanel.tsx:84:5` | B | t2 |
| 9 | `src/components/PermissionPanel/WebdavPermPanel.tsx:83:5` | B | t2 |
| 10 | `src/components/PresetEditor.tsx:60:7` | A | t3 |
| 11 | `src/components/ShareDetailDrawer.tsx:113:5` | A+B | t3 |
| 12 | `src/components/UserCreateModal.tsx:45:7` | A+B | t3 |
| 13 | `src/components/UserDetailDrawer.tsx:82:5` | A+B | t3 |
| 14 | `src/pages/Settings.tsx:1132:5` | C | t3 |

**数字勘误（验收项之一）**：`eslint.config.mjs:50-54` 与 `CHANGELOG.md`（"仍开放（专项级，见 docs/audit/00-backlog.md §3.5）：R-8 数据获取模式重构（复启 set-state-in-effect）"）留档写的是 **10 处**，本机实测为 **14 处**。多出的 4 处来自**批 1 新增代码**：

| 多出的 4 处 | 来源（批 1 交付） | 为什么批 1 留档没算 |
| --- | --- | --- |
| `DiagnoseModal.tsx:158` | 批 1 的一键诊断向导 | 该文件是批 1 新写，写时规则已 off，未纳入"26→10"的收敛计数 |
| `FirstRunGuide.tsx:52` | 批 1 的首启三问向导 | 同上 |
| `JournalDrawer.tsx:127` | 批 1 的操作回收站抽屉 | 同上 |
| `pages/Settings.tsx:1132` | 批 1 的告警规则卡（磁盘水位草稿） | 同上 |

结论：留档的"10 处"是**批 1 起点（规则降级时）的存量数**，不是本批次起点数；本批次起点是 **14 处**。t7 复启规则时，`eslint.config.mjs` 的留档注释必须按此纠正（14 处已清零 + 说明批 1 新增 4 处），不得继续沿用"10 处"。

### 1.2 分类汇总

| 类 | 定义 | 涉及点 |
| --- | --- | --- |
| **A** 纯本地态重置 | open / 目标变化时重置 tab、输入、草稿、状态位 | FirstRunGuide:52、GroupManageModal:63、PresetEditor:60、UserCreateModal:45、UserDetailDrawer:82；以及 DiagnoseModal:158、PermissionDrawer:118、ShareDetailDrawer:113 的本地态部分 |
| **B** 打开即取数 | 挂载/打开后同步 setState 触发异步加载 | FtpPermPanel:87、NfsPermPanel:84、WebdavPermPanel:83、JournalDrawer:127、PermissionMatrix:295、DiagnoseModal:158、PermissionDrawer:118、GroupManageModal:63、ShareDetailDrawer:113、UserCreateModal:45、FirstRunGuide:52 |
| **C** 派生态同步 | 用 effect 把外部值镜像进本地草稿 | pages/Settings.tsx:1132 |

> 多数点是 A+B 混合（同一 effect 里既重置本地态又发起取数）。**按 P-1 一个 effect 只能报一处**，所以清单看起来是一行，实际要按 §1.3 的完整清单处理。

### 1.3 逐点归档（含整段 effect 的完整同步 setState 清单）

每点格式：**报告行 / 所在 effect / 该 effect 内全部同步 setState / 主类 / 选定方案 / 理由 / 兄弟风险**。

#### 1. `DiagnoseModal.tsx:158:5` — `setShareName(initialShareName)`

- 所在 effect：`useEffect(..., [open, initialShareName])`（155-174）。
- 该 effect 内全部同步 setState：`setShareName(initialShareName)`(158)、`setItems(null)`(159)、`setRunError(null)`(160)、`setRunning(false)`(161)。**兄弟风险：159/160/161（P-1）**。
- 另有 `runIdRef.current += 1`(157)（ref 写，规则不报）、`call(() => api.adapter.list('smb')).then(...)`(163-170)（`.then` 内 `setShares` 在异步延续，按 P-3 不报）。
- 主类 **B+A**。
- 方案：A 部分 → `useResetOnKeyChange(\`${open}|${initialShareName ?? ''}\`, reset)`，reset 内只放 4 个 setState；B 部分 → 该 effect 保留，只承担 `runIdRef.current += 1` 与共享列表拉取，**不增删任何 IPC**。
- 理由：重置的是"打开/换目标就归零"的纯本地态（官方渲染期调整的教科书场景）；列表拉取今天已是异步安全的 `.then` 形态，无需改数据层。**`runIdRef` 的写必须留在 effect 内**（渲染期写 ref 违反 React 纪律，不能塞进 reset）。
- 兄弟风险处理：159/160/161 一并迁走；迁完该 effect 内**不允许再有任何 setState**。

#### 2. `FirstRunGuide.tsx:52:5` — `setStep(0)`

- 所在 effect：`useEffect(..., [open])`（50-71）。
- 该 effect 内全部同步 setState：`setStep(0)`(52)、`setPath('')`(53)、`setName('')`(54)、`setTier(DEFAULT_PERM_TIER)`(55)、`setAdvanced({})`(56)、`setErr('')`(57)。**兄弟风险：53-57**。
- 另有 `nameEdited.current = false`(58)（ref 写）与 `void api.disk.suggestRoot().then(...)`(62-67)（异步安全）。
- **注意**：同文件 74-76 的 `useEffect(..., [path])` 里有 `if (!nameEdited.current) setName(...)`，**不在** 14 条清单里——按 **P-4**（ref 守卫不报）。它不是本批次违例点，**也不得借 P-4 去"设计"新的 ref 守卫**。
- 主类 **A+B**。
- 方案：A 部分 → `useResetOnOpen(open, reset)`，reset 内放 52-57 的 6 个 setState；**`nameEdited` 由 ref 改为 state**（`const [nameEdited, setNameEdited] = useState(false)`，reset 内 `setNameEdited(false)`，输入框 `onChange` 里 `setNameEdited(true)`）；B 部分 → 该 effect 保留 `if (!open) return` + `suggestRoot()` 的 `.then` 拉取。
- 理由：把 `nameEdited.current = false` 放进渲染期 reset 会造成"渲染期写 ref"（React 明确不建议，reviewer 也会当瑕疵）。改 state 后语义不变（该标记只用于"路径变化是否覆盖名字"，今天也是在 effect 里被重置），且不再需要渲染期写 ref。
- 兄弟风险处理：52-57 与 58 一并处理。

#### 3. `GroupManageModal.tsx:63:7` — `setMembers(group.members || [])`

- 所在 effect：`useEffect(..., [open, group, form])`（60-71）。
- 该 effect 内全部同步 setState：`setMembers(...)`(63)、`setNewMember('')`(64)、`setRenaming(false)`(65)、`setNewGroupName('')`(66)、`setBatchMembers([])`(67)。**兄弟风险：64-67 与 69**。
- 另有 `form.setFieldsValue({ description: group.description })`(62)（antd 命令式 API，非 React state，规则不报，保留在 effect 内）与 `loadUsers()`(69)。
- **兄弟风险 69（P-3）**：`loadUsers`(50-57) 定义在**组件体**且内部含 `setAllUsers`，从 effect 调用它会被报。必须把取数逻辑**搬进 effect 回调内部**（J1/J2 形态）。
- 主类 **A+B**。
- 方案：A 部分 → `useResetOnKeyChange(\`${open}|${group?.name ?? ''}\`, reset)`，reset 内放 63-67 的 5 个 setState；表单同步保留在 effect 内（今天 62 行逐字不变）；B 部分 → effect 内定义
  ```tsx
  const load = async () => {
    try {
      const u = await call(api.user.list)
      if (!dead) setAllUsers(u)
    } catch {
      // 加载失败不影响主功能（既有语义，静默）
    }
  }
  ```
- 理由：成员/输入/重命名/批量选择全是纯本地态；用户列表是静默取数，按 B 类形态收进 effect 内部即零违例，IPC 次数与今天完全一致（每次 open/换组各一次）。

#### 4. `JournalDrawer.tsx:127:5` — `fetchList(useAppStore.getState().journal.length === 0)`

- 所在 effect：`useEffect(..., [open, loadJournal, message])`（123-129）。
- 该 effect 内唯一同步 setState 来自 `fetchList`(113-120) 的第一句 `if (showSpinner) setLoading(true)`——**组件体函数 + 同步 setState，正是 P-3 的报法**。**兄弟风险：无**。
- 语义要点（必须逐字保住）：**stale-while-revalidate**——"已有数据时不闪 loading，抽屉打开应立刻可读，而不是先白一下再出内容"。
- 主类 **B**。
- 方案：把 `loading=true` 的时机改成**渲染期**：`useResetOnOpen(open, () => { if (journal.length === 0) setLoading(true) })`（`journal` 已是本组件订阅值，读到的是最新快照，等价于今天的 `useAppStore.getState().journal.length === 0`）；effect 改为 async 安全形态，**loader 定义在 effect 内部**：
  ```tsx
  useEffect(() => {
    if (!open) return
    let dead = false
    const load = async () => {
      try {
        await loadJournal()
      } catch (e) {
        if (!dead) message.error((e as Error).message)
      } finally {
        if (!dead) setLoading(false)
      }
    }
    void load()
    return () => { dead = true }
  }, [open, loadJournal, message])
  ```
  `fetchList` 删除（只服务这一处；loading 落回已在 loader 的 `finally`）。
- 理由：`setLoading(true)` 必须在"打开那一刻"生效，而 effect 里的同步 setState 正是违例来源；渲染期调整是 P-5 允许且官方推荐的形态。改完后：打开且 store 为空 → 转圈；打开且 store 有数据 → 不转圈、后台静默刷新；关闭再打开 → 各恰好 1 次 `loadJournal`。
- 为什么不用 react-query：见 §1.4 否决理由第 1 条（`staleTime: 3_000` 会让 3 秒内重开完全不发请求，破坏"打开即拉取"）。

#### 5. `PermissionDrawer.tsx:118:7` — `loadPerms()`

- 所在 effect：`useEffect(..., [open, share])`（115-124）。
- 该 effect 内全部同步 setState 来源：`loadPerms()`(118)、`loadCandidates()`(119)（两者都是组件体函数，含 setState）、`setNtfs(null)`(120)。**兄弟风险：119、120（P-3/P-1）**。
- 主类 **B+A**。
- 方案：A 部分 → `useResetOnKeyChange(\`${open}|${share?.name ?? ''}|${share?.protocol ?? ''}|${nonce}\`, reset)`，reset 内放 `setNtfs(null)` 与（仅 `share?.protocol === 'smb'` 时）`setLoading(true)`；B 部分 → effect 内定义 `loadPerms`/`loadCandidates` 两个 loader（搬进 effect 内部），SMB 分支 `void loadPerms(); void loadCandidates()`，`dead` 守卫见 §1.6 披露 2；「重新加载」按钮（253）与保存成功后重读（155）统一改为 `const reload = () => setNonce((n) => n + 1)`，`nonce` 纳入渲染期 key 与 effect 依赖。
- 理由：`rows` 是可编辑本地态，**不引入服务端/草稿双态**（保持今天"取数直接落 rows、本地编辑直接改 rows"）；`ntfs` 是纯本地态归零；候选账号是静默取数。四条取数路径（打开/换共享/重新加载/保存后重读）共用同一实现，各恰好一次 IPC。

#### 6. `PermissionMatrix.tsx:295:5` — `load()`

- 所在 effect：`useEffect(() => { load(); return () => { cancelRef.current = true } }, [])`（294-301）。
- 该 effect 内唯一同步 setState 来自 `load`(244-292) 的**第一句** `setLoading(true)`(245)；`load` 定义在组件体 → 按 P-3 在调用点报。**兄弟风险：无**。
- 必须保住的既有语义：`cancelRef` + `mapPool` 的 `shouldCancel` 取消机制、渐进渲染（`setShares`/`setAccounts` 在第一个 await 后先落地、`setMatrix` 最后落地）、`cancel()` 置 `loading=false` + `message.info('已取消加载')`、导出按钮 `disabled={loading || !shares.length}`。
- 主类 **B**。
- 方案（只动"loading 初值 + pre-await 路径"）：
  1. `const [loading, setLoading] = useState(true)`（该组件挂载即加载，`true` 才是真实初值）。
  2. `load` 里**删掉** `setLoading(true)`(245)，其余逐字不动（`cancelRef.current = false`(246) 留在原位；`setShares`/`setAccounts`/`setMatrix`/`finally setLoading(false)` 全在 await 之后，按 P-3 不报）。
  3. 「重新加载」按钮（395）改为事件处理器先置位：`onClick={() => { setLoading(true); void load() }}`（事件处理器里的 setState 不受本规则约束）。
- 理由：`load` 的 pre-await 路径清空后，effect 调用它零违例；渐进渲染、取消、导出禁用、IPC 次数全部逐字保留。**这是本批次唯一一处把 loading 初值改成 `true` 的挂载型组件**，可见影响见 §1.6 披露 1。
- 为什么不用 react-query：今天"取消"保留已提交的 `shares`/`accounts`（导出按钮因此可用）；换 react-query 后取消会回滚到未提交状态，表格变空、导出按钮变禁用——**可观察的行为变化**，本批次不允许。

#### 7-9. `FtpPermPanel.tsx:87:5` / `NfsPermPanel.tsx:84:5` / `WebdavPermPanel.tsx:83:5` — `load()`

- 三文件同构。所在 effect：`useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [share.name])`（Ftp 86-90 / Nfs 83-87 / Webdav 82-86）。
- 该 effect 内唯一同步 setState 来自各自 `load()`（组件体）的第一句 `setLoading(true)`（Ftp:75 / Nfs:72 / Webdav:71）。**兄弟风险：无**。
- 主类 **B**，且是 R-5 抽象的主要落点。
- 方案：整段交给 §2.1 的 `useProtocolPermissions`。三面板不再各自持有 `rows/loading/saving/load/handleAdd/handleRemove/handlePermChange/handleTypeChange/handleSave`；只保留协议特有的映射函数、列定义、选项文案、空态文案、添加区控件与提示文案（§2.5）。**对外 props 仍为 `{ share }`**（§2.0 边界 1）。
- 理由：三处逐字同构的 `load/save/rows` 模板正是 R-5 要消灭的重复；把"取数 + 本地编辑 + 保存覆盖 + 重读"收进一个 hook 后，三处违例一次归零，且"切换共享重载 / 保存后重读 / 失败不假成功"由同一实现保证，回归矩阵（§3）可对三面板复用同一组断言。

#### 10. `PresetEditor.tsx:60:7` — `setEntries(...)`

- 所在 effect：`useEffect(..., [open, preset, form])`（53-66）。
- 该 effect 内全部同步 setState：`setEntries(...)`(60)、`setNewAccount('')`(61)、`setNewType('User')`(62)、`setNewAccess('Read')`(63)、`setNewDeny(false)`(64)。**兄弟风险：61-64**。
- 同文件 69-94 的候选账号 effect **不在清单内**：`loadCandidates` 定义在 effect **内部**，`setCandidates` 在 `await Promise.all(...)` 之后 → 按 P-3 不报。**该 effect 保持原样，一个字都不要改。**
- 主类 **A**。
- 方案：`useResetOnKeyChange(\`${open}|${preset?.id ?? preset?.name ?? ''}\`, reset)`，reset 内放 60-64 的 5 个 setState；`form.setFieldsValue({...})`(55-59) 留在 effect 内（非 state）。
- 理由：`preset.entries` 深拷贝进 `entries` 是本地草稿初始化，属官方"props 变化重置 state"场景；候选账号 effect 已是异步安全形态，不动它可把改动面压到最小。

#### 11. `ShareDetailDrawer.tsx:113:5` — `setTab('info')`

- 所在 effect：`useEffect(..., [open, share])`（111-142）。
- 该 effect 内全部同步 setState 来源：`setTab('info')`(113)、`loadConnections()`(115)、`loadOpenFiles()`(116)（后两者是组件体函数）。`form.setFieldsValue(...)`(118-139) 非 state。**兄弟风险：115、116（P-3）**。
- 必须保住的既有语义：`loadConnections` 失败 → `message.error`；`loadOpenFiles` 失败 → **静默** `setOpenFiles([])`；非 SMB 共享**不**拉连接/打开文件（只按协议预填表单）。
- 主类 **A+B**。
- 方案：A 部分 → `useResetOnKeyChange(\`${open}|${share?.name ?? ''}|${nonce}\`, reset)`，reset 内放 `setTab('info')` 与（仅 SMB 分支）`setLoading(true)`；表单同步保留在 effect 内（按协议分支逐字不变）；B 部分 → effect 内定义 `loadConnections`/`loadOpenFiles`（搬进 effect 内部），SMB 分支 `void loadConnections(); void loadOpenFiles()`；「刷新连接」与「关闭全部打开文件」后的重读统一改为 `nonce` 渲染期重读（事件处理器只 `setNonce(n => n+1)`）。
- 理由：`tab` 是纯本地态；连接/打开文件是"打开即取数"，收进 effect 内部即零违例；非 SMB 降级分支与"打开文件失败静默"逐字保留。

#### 12. `UserCreateModal.tsx:45:7` — `setSelectedGroups([])`

- 所在 effect：`useEffect(..., [open, form])`（37-49）。
- 该 effect 内全部同步 setState 来源：`setSelectedGroups([])`(45)、`setPwdStrength(null)`(46)、`loadGroups()`(47)（组件体函数，内部 `setGroups` 在 await 之后，但按 P-3 仍在调用点报）。**兄弟风险：46、47**。
- 主类 **A+B**。
- 方案：A 部分 → `useResetOnOpen(open, reset)`，reset 内放 `setSelectedGroups([])`、`setPwdStrength(null)`；表单部分保留在 effect 内：`useEffect(() => { if (open) { form.resetFields(); form.setFieldsValue({ enabled: true, userMayChangePassword: true, passwordNeverExpires: true }) } }, [open, form])`（今天 39-44 逐字不变）；B 部分 → `loadGroups` 搬进 effect 内部（`await call(api.user.groups)` → `if (!dead) setGroups(g)`，**失败静默**）。
- 理由：纯本地态重置 + 静默取数，最小改动即零违例；"创建失败不清空已填"与"两次密码不一致不提交"两条既有行为不受影响（都在事件处理器里）。

#### 13. `UserDetailDrawer.tsx:82:5` — `setTab('props')`

- 所在 effect：`useEffect(..., [open, user, form])`（80-93）。
- 该 effect 内全部同步 setState：`setTab('props')`(82)、`setPwdOpen(false)`(91)、`setNewPwd('')`(92)。**兄弟风险：91、92**。
- `form.setFieldsValue(...)`(83-90) 非 state；组列表/共享权限的加载在 Tab 切换的事件处理器里（不在本 effect 内），**不动**。
- 主类 **A**。
- 方案：`useResetOnKeyChange(\`${open}|${user?.name ?? ''}\`, reset)`，reset 内放 82/91/92 三个 setState；表单同步留在 effect 内。
- 理由：三处都是"打开/换用户就归零"的纯本地态；该组件不含取数违例，改动面最小。

#### 14. `pages/Settings.tsx:1132:5` — `setDiskDraft(rules.diskLowGb)`

- 所在 effect：`useEffect(() => { setDiskDraft(rules.diskLowGb) }, [rules.diskLowGb])`（1131-1133），在 `AlertRulesCard` 内。
- 该 effect 内唯一同步 setState：1132。**兄弟风险：无**。
- 必须保住的既有语义：`diskDirty = diskDraft !== null && diskDraft !== rules.diskLowGb`；阈值未改动不提交；保存失败时 `setDiskDraft(rules.diskLowGb)` 退回当前值（1143）。
- 主类 **C**。
- 方案：删除该 effect，改为渲染期调整：
  ```tsx
  const [diskDraft, setDiskDraft] = useState<number | null>(rules.diskLowGb)
  useResetOnKeyChange(`disk:${String(rules.diskLowGb)}`, () => setDiskDraft(rules.diskLowGb))
  ```
  （`useResetOnKeyChange` 来自 §2.0 白名单的 `src/hooks/useResetOnOpen.ts`，t3 拥有。）
- 理由：官方文档"props/外部值变化时调整 state"的原型场景（`You Might Not Need an Effect` 的 Adjusting state when a prop changes）。渲染期调整后：外部值变化 → 草稿跟随（下一帧即生效，不再多一次渲染）；用户改动 → dirty；保存失败 → 退回。三条既有语义逐字不变。
- 备选（不采用）：完全派生（去掉草稿）——会破坏"编辑中不被外部刷新打断"的体验，且保存失败回滚语义会消失。

### 1.4 三类各选定的唯一方案（含被否决方案与理由）

#### A 类（纯本地态重置）→ **渲染期调整 state**，载体 `useResetOnOpen` / `useResetOnKeyChange`

- 实现：`src/hooks/useResetOnOpen.ts`（modals-dev 拥有，§2.0 白名单）。
- 理由：
  1. **P-5 实测**：渲染期 setState（含自定义 hook 内的 reset 回调）不被该规则报，且是 React 官方文档明确的模式（"adjusting state during render"，`You Might Not Need an Effect`）。
  2. **不改变挂载/卸载时机**：11 个 modal/drawer 的挂载点分散在 `App.tsx`（FirstRunGuide）、`pages/Settings.tsx`（DiagnoseModal 懒加载）、`pages/Shares.tsx` 与 `SharesWithOps.tsx`（其余抽屉/弹窗）。用 `key` 重挂载要在这些父组件逐处铺 `key={...}`，会改变关闭动画与 IPC 时机（把"打开时的一次请求"变成"重挂载时的一次请求"，并丢掉 antd 的关闭过渡）。
  3. 三 PermPanel 已经由 `PermissionDrawer` 的 `destroyOnClose` 天然获得"关闭即卸载、重开即重置"的效果，**无需** key 重挂载。
- **否决**：`key` 重挂载（理由 2）；`useLayoutEffect` 迁移（P-6 同样被报，属规避）；ref 守卫静音（P-4，属规避，§0.4 红线 3 禁止）。

#### B 类（打开即取数）→ **"effect 内定义 loader" 的 async 安全形态**（渲染期调整 loading + effect 内 loader + 异步延续里写 state）

三条硬约束（缺一即不合规）：

- **B-1 载入函数必须定义在 effect 回调内部**（P-3：定义在组件体/`useCallback` 的同类函数会在调用点被报，即使其 setState 在 await 之后）。
- **B-2 loader 的第一个语句即 `await`**；任何 setState（含 `catch`/`finally`）都必须在 await 之后。禁止在 pre-await 路径写 state。
- **B-3 "加载中"标志不得依赖 pre-await 的 `setState(true)`**：挂载即加载的组件用 `useState(true)`（仅 PermissionMatrix）；由 open/目标切换触发的用渲染期调整（A 类同一载体）；"重新加载/保存后重读"用**渲染期重读序号 `nonce`**（事件处理器只 bump 序号）。

已实测的参考实现（lint 零违例）：

```tsx
// 渲染期重读序号 + effect 内 loader：打开 / 换目标 / 重新加载 / 保存后重读 四条路径共用一次取数
const [nonce, setNonce] = useState(0)
const loadKey = `${protocol}|${shareName}|${nonce}`
useResetOnKeyChange(loadKey, () => setLoading(true))
useEffect(() => {
  let dead = false
  const load = async () => {
    try {
      const list = await call(() => api.adapter.permissions(protocol, shareName))
      if (!dead) setRows(list.map(toRow))
    } catch (e) {
      if (!dead) message.error((e as Error).message)
    } finally {
      if (!dead) setLoading(false)
    }
  }
  void load()
  return () => { dead = true }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- 有意仅依赖 loadKey（协议|目标|重读序号）：toRow/message 为模块级/上下文稳定引用，纳入依赖会重复触发
}, [loadKey])
const reload = () => setNonce((n) => n + 1)
```

理由：

1. **IPC 调用次数与参数逐字不变**：不引入任何缓存层。这对"行为零变化"是决定性的（见下面否决 react-query 的第 1 条）。
2. **保留今天的关键语义**：PermissionMatrix 的取消 + 渐进渲染、JournalDrawer 的 stale-while-revalidate、ShareDetailDrawer"打开文件失败静默"、各面板"加载失败 message.error 且旧数据不丢"。
3. **规则可机械验证**：该形态在 lint 下零违例（§0.4 的 J1/J2/J4/J5 探针），规则复启为 error 后任何回退都会被挡住。

**否决：react-query**（尽管批 1 已用它迁移 5 页面 + 3 SettingsPanel，本批次**不**把这 14 点迁过去）：

1. `App.tsx:21-28` 的 `QueryClient` 默认是 `retry: 0 / refetchOnWindowFocus: false / **staleTime: 3_000**`。`staleTime: 3s` 意味着**关闭再打开若在 3 秒内将完全不发请求**——直接违反"打开即取数、每次打开恰好一次"，与 `PermissionDrawer` 的 `destroyOnClose`（每次打开都重新挂载）叠加后差异更明显。要修就得逐 query 覆写 `staleTime: 0`，等于放弃缓存收益却承担其复杂度。
2. 默认 `refetchOnReconnect: true` 会在网络事件上多发 IPC，是"IPC 次数不变"的隐性破坏点。
3. 取消语义不兼容：PermissionMatrix 的"取消"今天保留已提交的 `shares/accounts`（导出按钮因此可用），react-query 取消会回滚未提交数据 → 表格空、按钮禁用，属可观察行为变化。
4. `rows` 是可编辑草稿：迁到 react-query 必须引入"服务端 + 草稿"双态，多一层状态与一条重置路径，与"最小行为风险"目标相反。

**例外（保持既有用法不变）**：三 SettingsPanel 的 `useQuery` **不动**（不在 14 条违例内）；R-5 的 settings hook 只是把既有 `useQuery` 用法原样收进去（§2.3）。原因：它们的 `load()` 是 `invalidateQueries`（**setState-free**），因此可安全被 `useTickEffect` 的 effect 调用——若把 `load` 改成 setState 型重载，会立刻触发新的 `set-state-in-effect` 违例。

#### C 类（派生态同步）→ **渲染期调整 state**（与 A 类同一载体），禁止 `useState + useEffect` 镜像

- 唯一落点：`pages/Settings.tsx:1132`（方案与理由见 §1.3 第 14 点）。
- 理由：C 类本质是 A 类的特例（外部值 → 本地草稿），同一载体让整批次只有"渲染期调整"与"effect 内 loader"两种手法，便于 reviewer 逐文件判定。
- **否决**：保留 effect + ref 守卫（P-4 能静音，但那是规避手法，§0.4 红线 3）；完全派生去掉草稿（破坏编辑中不被打断 + 保存失败回滚）。

### 1.5 实现纪律（t2/t3 必须遵守）

1. **整段处理**：报告行只是该 effect 的第一处同步 setState（P-1）。按 §1.3 清单把该 effect 内的 setState 全部处理完，再重跑 lint。
2. **逐文件零违例**：每改完一个文件立刻跑 `node_modules/.bin/eslint <该文件> --rule '{"react-hooks/set-state-in-effect":"error"}'`，必须 exit 0。
3. **不允许 `set-state-in-effect` 的行内 disable**；`exhaustive-deps` 的 disable 必须带具体理由（仓库既有风格）。
4. **禁止 §0.4 红线 1-3 的手法**（延时/微任务/`useLayoutEffect` 换壳/把 state 搬进 ref 静音）。
5. **每次改完跑全量门禁**：`pnpm typecheck` / `pnpm lint` / `pnpm format:check` / `pnpm test` 全 exit 0。
6. **不动 14 点以外的 effect**：特别点名——`PresetEditor.tsx:69-94`（候选账号）、`FirstRunGuide.tsx:74-76`（路径带名，P-4 未报）、`UserDetailDrawer` 的 Tab 加载处理器、三个 `*SettingsPanel` 的既有 `useEffect`，都不在违例内，**不得顺手重写**。

### 1.6 允许的语义收紧（必须在评审中显式声明，不得当成"零变化"隐瞒）

1. **打开/切换瞬间的 loading 标志在同一次提交内生效**：今天 `loading=true` 是 effect 里的同步 setState，会先渲染一帧"非加载态/空态"再切到加载态（这正是本规则要消除的 cascading render）。改后首次以"打开态"渲染时即显示加载态。可见影响：不再闪一帧空态（PermissionMatrix 的"暂无共享"、三 PermPanel 的"暂无客户端规则"等）。**断言口径**：加载中不得出现空态文案（§3.3）。
2. **竞态保护（新增 `dead` 守卫）**：卸载或换目标后，迟到的响应不再写入 state。今天的行为是"后到者覆盖"，快速切换目标时可能显示旧目标数据；改后旧响应被丢弃。这是 t4 被要求主动寻找的"竞态导致的陈旧数据"类问题的正解；§3 的"切换目标→重载"用例必须断言新目标数据最终胜出。
3. **不新增也不减少 IPC**：B 类实现不引入缓存，因此"打开 → 恰好一次"、"换目标 → 恰好一次"、"保存成功 → 保存 1 次 + 重读 1 次"与今天逐字一致。**不使用 react-query 正是为了保住这一条**。
4. 除上述 1、2 两条，**文案、空态、降级分支、IPC 参数、权限模型映射、列定义、tab 默认值一律零变化**。

---

## 2. R-5 全量抽象：对外 API

### 2.0 硬性边界（实现者直接遵守，不得临场决定）

**边界 1 — 三 PermPanel 对外 props 形状不得改变**：

```ts
interface Props { share: Share }   // NfsPermPanel / FtpPermPanel / WebdavPermPanel 三者一律如此
```

不得新增/删除/改名 props，不得要求 `open`、`protocol`、`onSaved` 等。原因：`PermissionDrawer.tsx:234-239` 按协议渲染 `<NfsPermPanel share={share} />` 等；三面板与 PermissionDrawer 的改动不得交叉（t2 的 outOfScope 明确含 `PermissionDrawer.tsx`，t3 的 outOfScope 明确含 `PermissionPanel/**`）。

**边界 2 — 本批次允许新增的共享 hook / 展示组件文件，只有下列 5 个（白名单之外一律不允许新增）**：

| 路径 | 导出 | 归属 | 来源 |
| --- | --- | --- | --- |
| `src/hooks/useProtocolPermissions.ts` | `useProtocolPermissions` | **panels-dev**（t2） | 任务给定 |
| `src/hooks/useResetOnOpen.ts` | `useResetOnOpen`、`useResetOnKeyChange` | **modals-dev**（t3） | 任务给定 |
| `src/hooks/useProtocolSettings.ts` | `useProtocolSettings` | **panels-dev** | **本规格追加声明** |
| `src/components/ProtocolPermissionShell.tsx` | `ProtocolPermissionShell` | **panels-dev** | **本规格追加声明** |
| `src/components/ProtocolSettingsShell.tsx` | `ProtocolSettingsShell` | **panels-dev** | **本规格追加声明** |

追加声明这 3 个文件的理由：

- 三 SettingsPanel 的共享逻辑（协议门控/配置读取/保存/服务启停/恢复默认/刷新 tick）在 t2、t3 的 inScope 里**都没有归属**；不显式指定，R-5 的"三 SettingsPanel"半边就无人落地。
- 两个 Shell 是"展示组件"这一半的落点。只抽 hook 不抽展示组件，三面板各自仍留约 55-65 行逐字相同的 JSX（Spin 包裹、头部说明栏 + 重新加载/保存按钮、Table 外壳、添加区容器、服务状态 Descriptions、底部提示），reviewer 会按"抽象是否真的减少了重复"（t5 判定要点 1）打回。
- 归属统一给 **panels-dev**：R-5 两半（PermPanel / SettingsPanel）共享同一套"hook + shell"模式与同一套测试手法，交给一个 owner 可避免两个 owner 对同一模式写出两种实现。
- **待 captain 处理**：t2 的 inScope 目前只有 `src/components/PermissionPanel/**` 与 `src/hooks/useProtocolPermissions.ts`；t3 的 outOfScope 含 `src/hooks/useProtocolPermissions.ts`。要让上面 3 个追加文件落地，必须由 captain **扩展 t2 的 inScope**（加入 `src/hooks/useProtocolSettings.ts`、`src/components/ProtocolPermissionShell.tsx`、`src/components/ProtocolSettingsShell.tsx`、`src/components/NfsSettingsPanel.tsx`、`src/components/FtpSettingsPanel.tsx`、`src/components/WebdavSettingsPanel.tsx`、`src/components/ProtocolSettingsPanels.test.tsx`），或另开一个 owner 明确的实现任务。**不得留给实现者临场决定。**

**边界 3 — 不得新增白名单外的其他共享文件**：实现过程中若发现还需要第 6 个文件，必须停下来报 captain，不得自行创建。

### 2.1 三 PermPanel 的共享 hook：`useProtocolPermissions`

**文件**：`src/hooks/useProtocolPermissions.ts`（panels-dev 拥有）

```ts
import type { Protocol, SharePermission } from '../types'

/** 三协议面板共用的行基形状（Nfs 用 clientName 作 key，Ftp/Webdav 用 account） */
export interface ProtocolPermissionRowBase {
  account: string
  accountType: 'User' | 'Group'
}

export interface UseProtocolPermissionsOptions<TRow> {
  /** 权限通道（决定 IPC 通道；不参与行结构） */
  protocol: Extract<Protocol, 'nfs' | 'ftp' | 'webdav'>
  /** 目标共享名；变化即重载（等价今天的 [share.name]） */
  shareName: string
  /** adapter 返回 → 面板行。**必须是模块级函数**（引用稳定，避免重复 IPC） */
  toRow: (p: SharePermission) => TRow
  /** 面板行 → adapter 入参。**必须是模块级函数**；签名由 (share, row) 改为 (row, shareName)（见 §2.5） */
  toPerm: (row: TRow, shareName: string) => SharePermission
  /** 行主键：'clientName' | 'account' */
  rowKey: keyof TRow & string
}

export interface UseProtocolPermissionsResult<TRow> {
  /** 当前行（服务端返回即落此处；本地编辑直接改此处） */
  rows: TRow[]
  /** 取数中（挂载即 true） */
  loading: boolean
  /** 保存中 */
  saving: boolean
  /** 事件入口：重新加载 / 保存成功后重读。**禁止在 effect 中调用**（内部 bump 渲染期重读序号） */
  reload: () => void
  /** 事件入口：保存覆盖。成功 → true，并内部 message.success(successText) + reload()；失败 → false，并 message.error(原因) */
  save: (successText: string) => Promise<boolean>
  /** 本地新增一行（校验与提示文案留在面板） */
  addRow: (row: TRow) => void
  /** 本地删除一行 */
  removeRow: (key: string) => void
  /** 本地改一行（浅合并） */
  updateRow: (key: string, patch: Partial<TRow>) => void
  /** 行是否已存在（面板用它给出"该账号/客户端已存在"提示） */
  hasRow: (key: string) => boolean
}

export function useProtocolPermissions<TRow>(
  opts: UseProtocolPermissionsOptions<TRow>,
): UseProtocolPermissionsResult<TRow>
```

**实现契约（照做，不留自由度）**

1. 取数 `api.adapter.permissions(protocol, shareName)`；保存 `api.adapter.setPermissions(protocol, shareName, rows.map((r) => toPerm(r, shareName)))`。**IPC 参数与今天逐字相同**。
2. `rows` 是唯一真源：服务端结果直接 `setRows(list.map(toRow))`；本地编辑直接改 `rows`。**不引入服务端/草稿双态**。
3. `loading` 初值 `true`（三面板仅在 `PermissionDrawer` 打开且协议匹配时挂载，挂载即加载）。
4. 目标切换：`const loadKey = \`${protocol}|${shareName}|${nonce}\``；渲染期调整 `if (prevKey !== loadKey) { setPrevKey(loadKey); setLoading(true) }`；**不清空 `rows`**（与今天一致：旧数据在新数据到达前仍可见，只是盖上 `Spin`）。
5. effect 依赖 `[loadKey]` + 一条带理由的 `exhaustive-deps` disable（理由：`toRow`/`toPerm` 为模块级稳定引用、`message` 为上下文稳定引用，纳入依赖会重复触发）。**该 disable 只许抑制 `exhaustive-deps`；本文件 `set-state-in-effect` 零违例、零 disable。**
6. `reload()` = `setNonce((n) => n + 1)`。所以"保存后重读"与"按钮重读"走同一条 effect 路径，各恰好一次 IPC。
7. `save(successText)` 顺序（与今天逐字一致）：`setSaving(true)` → `await setPermissions(...)` → `message.success(successText)` → `reload()` → resolve `true`；`catch` → `message.error((e as Error).message)` → resolve `false`（**不改 `rows`，不做乐观更新，不假成功**）；`finally` → `setSaving(false)`。
8. `message` 取自 `App.useApp()`（hook 内调用，三面板都在 `App` 上下文内）。
9. 竞态：`let dead = false`，cleanup 置位；异步延续里的写入一律 `if (!dead)`（§1.6 披露 2）。
10. **错误语义总表**：取数失败 → `message.error(原因)`，`rows` 保持上一次值，`loading` 落回 `false`；保存失败 → `message.error(原因)`，`saving` 落回 `false`，`rows` 不动（用户可改后重试）；`toRow`/`toPerm` 自身抛错按取数/保存失败处理，不吞。

### 2.2 三 PermPanel 的展示组件：`ProtocolPermissionShell`

**文件**：`src/components/ProtocolPermissionShell.tsx`（panels-dev 拥有）

```tsx
import type { ReactElement, ReactNode } from 'react'
import type { TableColumnsType } from 'antd'

export interface ProtocolPermissionShellProps<TRow> {
  loading: boolean
  saving: boolean
  /** 头部说明（协议特有文案，可含 <code>） */
  headerText: ReactNode
  /** 「重新加载」 */
  onReload: () => void
  /** 「保存」 */
  onSave: () => void
  /** 保存二次确认标题（协议特有：确认覆盖当前 NFS 客户端权限？ / FTP 授权规则？ / WebDAV 作者规则？） */
  saveConfirmTitle: string
  columns: TableColumnsType<TRow>
  rows: TRow[]
  rowKey: string
  /** 空态文案（协议特有：暂无客户端规则 / 暂无授权规则 / 暂无作者规则） */
  emptyText: string
  /** 添加区标题（协议特有：添加客户端规则 / 添加授权规则 / 添加作者规则） */
  addTitle: string
  /** 添加区控件（协议特有：Input/Select/Button 组合） */
  addBox: ReactNode
  /** 添加区底部提示（协议特有：Tag + 说明句） */
  hint: ReactNode
}

export function ProtocolPermissionShell<TRow>(p: ProtocolPermissionShellProps<TRow>): ReactElement
```

**DOM 契约（必须逐字复现今天的结构；测试按可见文本断言）**

```
<Spin spinning={loading}>
  <div className="mb-3 flex items-center justify-between">
    <span className="text-sm text-fog">{headerText}</span>
    <Space>
      <Button size="small" icon={<ReloadOutlined />} onClick={onReload}>重新加载</Button>
      <Popconfirm title={saveConfirmTitle} onConfirm={onSave}>
        <Button size="small" type="primary" loading={saving}>保存</Button>
      </Popconfirm>
    </Space>
  </div>
  <Table dataSource={rows} rowKey={rowKey} columns={columns} pagination={false} size="small"
         locale={{ emptyText: <Empty description={emptyText} /> }} />
  <div className="mt-4 p-3 rounded-card bg-white/60">
    <div className="text-xs text-fog mb-2">{addTitle}</div>
    <Space wrap>{addBox}</Space>
    <div className="mt-2 text-xs text-fog">{hint}</div>
  </div>
</Spin>
```

> 今天的 `addBox` 外层已有 `<Space wrap>`（Nfs:203 / Ftp:213 / Webdav:192），归 Shell 负责；面板传进去的是"控件数组"。

### 2.3 三 SettingsPanel 的共享 hook：`useProtocolSettings`

**文件**：`src/hooks/useProtocolSettings.ts`（panels-dev 拥有）

```ts
import type { FormInstance } from 'antd'
import type { Protocol, ServiceStatus } from '../types'

export interface ProtocolServiceApi<TConfig> {
  getConfig: () => Promise<TConfig>
  serviceStatus: () => Promise<ServiceStatus>
  setConfig: (patch: Partial<TConfig>) => Promise<void>
  restoreDefault: () => Promise<TConfig>
  restart: () => Promise<void>
  start: () => Promise<void>
  stop: () => Promise<void>
}

export interface ProtocolSettingsTexts {
  /** '已保存' */
  saved: string
  /** 'NFS 服务已重启' / 'FTP 服务已重启' / 'WebDAV 服务已重启' */
  restarted: string
  /** 'NFS 服务已启动' … */
  started: string
  /** 'NFS 服务已停止' … */
  stopped: string
  /** '已恢复默认配置' */
  restored: string
}

export interface UseProtocolSettingsOptions<TConfig> {
  protocol: Extract<Protocol, 'nfs' | 'ftp' | 'webdav'>
  api: ProtocolServiceApi<TConfig>
  texts: ProtocolSettingsTexts
}

export interface UseProtocolSettingsResult<TConfig> {
  /** 面板用 <Form form={settings.form}> 渲染协议特有字段 */
  form: FormInstance<TConfig>
  /** true=已安装 / false=未安装（走降级分支）/ null=探测中 */
  installed: boolean | null
  /** 最近一次成功读取的配置（未取到时 undefined） */
  config: Partial<TConfig> | undefined
  /** 服务状态（未取到时 null） */
  service: ServiceStatus | null
  /** = isFetching */
  loading: boolean
  saving: boolean
  /** = invalidateQueries（**setState-free**，因此可安全被 useTickEffect 的 effect 调用） */
  load: () => void
  save: () => Promise<void>
  restoreDefault: () => Promise<void>
  restart: () => Promise<void>
  start: () => Promise<void>
  stop: () => Promise<void>
}

export function useProtocolSettings<TConfig>(
  opts: UseProtocolSettingsOptions<TConfig>,
): UseProtocolSettingsResult<TConfig>
```

**实现契约（把今天三份逐字同构的代码原样收进来，不改数据层）**

1. `useEnsureProtocolCaps({ onDetectFailure: () => setDetectFailed(true) })`；`installed = detectFailed ? false : protocolCaps ? !!protocolCaps[protocol]?.installed : null`。
2. `useQuery({ queryKey: [\`${protocol}-settings\`], queryFn: async () => { const [c, s] = await Promise.all([api.getConfig(), api.serviceStatus()]); return { c, s } }, enabled: installed === true })`。**queryKey 逐字沿用今天的 `'nfs-settings' | 'ftp-settings' | 'webdav-settings'`**，不新增、不共享 key。
3. `useEffect(() => { if (error && !data && installed === true) message.error((error as Error).message) }, [error, data, installed])`——保留今天那条 `exhaustive-deps` disable 及理由。
4. `useEffect(() => { if (data) form.setFieldsValue(data.c) }, [data, form])`。
5. `useTickEffect(refreshTick, () => { if (installed === true) load() })`；`load = () => void queryClient.invalidateQueries({ queryKey: [\`${protocol}-settings\`] }).catch(() => {})`。
6. `save()`：**先 `const v = await form.validateFields()`（在 `try` 之外、`setSaving(true)` 之前）**——校验失败不弹 `message`，与今天逐字一致；然后 `setSaving(true)` → `await call(() => api.setConfig(v))` → `message.success(texts.saved)` → `load()` → `finally setSaving(false)`；失败 → `message.error(原因)`，**不回滚表单**（今天也不回滚）。
7. `restart`/`start`/`stop`：`await call(api.restart|start|stop)` → `message.success(texts.xxx)` → `load()`；失败 → `message.error(原因)`，不 `load`。
8. `restoreDefault`：`const def = await call(api.restoreDefault)` → `message.success(texts.restored)` → **`form.setFieldsValue(def)`** → `load()`；失败 → `message.error(原因)`。（`setFieldsValue(def)` 是今天的即时反馈，保留；随后的 `load()` 会再按服务端值同步一次。）
9. **错误语义总表**：探测失败 → `installed=false`（降级渲染），不抛；首轮配置读取失败 → `message.error(原因)`，表单保持空；`save`/`restoreDefault`/`restart`/`start`/`stop` 失败 → `message.error(原因)` 且不做任何乐观变更；`load()` 永不抛（内部 `catch(() => {})`）。

### 2.4 三 SettingsPanel 的展示组件：`ProtocolSettingsShell`

**文件**：`src/components/ProtocolSettingsShell.tsx`（panels-dev 拥有）

```tsx
import type { ReactElement, ReactNode } from 'react'
import type { ServiceStatus } from '../types'

export interface ProtocolSettingsActions {
  saving: boolean
  save: () => void
  restoreDefault: () => void
  /** '确认恢复 NFS 默认配置？' 等 */
  restoreConfirmTitle: string
  restart: () => void
  /** '重启 NfsService 服务？' / '重启 ftpsvc 服务？' 等 */
  restartConfirmTitle: string
  start: () => void
  stop: () => void
  /** '停止 NfsService 服务？' 等 */
  stopConfirmTitle: string
  load: () => void
}

export interface ProtocolSettingsShellProps {
  loading: boolean
  service: ServiceStatus | null
  actions: ProtocolSettingsActions
  /** ConfigPresetBar 等头部插槽（协议特有 presets） */
  header?: ReactNode
  /** 面板自己的 <Form form={settings.form} layout="vertical">…协议特有字段…</Form> */
  children: ReactNode
  /** 底部说明（协议特有） */
  footer: ReactNode
}

export function ProtocolSettingsShell(p: ProtocolSettingsShellProps): ReactElement
```

**DOM 契约**（`installed === true` 时渲染；未安装/探测中由面板自己早返回，见 §2.5）：

```
<Spin spinning={loading}>
  <div className="glass-card p-4">
    {header}
    {children}
    <Space className="mt-4 flex-wrap">
      <Button type="primary" loading={saving} onClick={save}>保存配置</Button>
      <Popconfirm title={restoreConfirmTitle} okText="恢复默认" okType="danger" cancelText="取消" onConfirm={restoreDefault}>
        <Button icon={<UndoOutlined />} danger>恢复默认</Button>
      </Popconfirm>
      <Popconfirm title={restartConfirmTitle} onConfirm={restart}>
        <Button icon={<PoweroffOutlined />}>重启服务</Button>
      </Popconfirm>
      {service?.status === 'Stopped'
        ? <Button icon={<CaretRightOutlined />} onClick={start}>启动</Button>
        : <Popconfirm title={stopConfirmTitle} onConfirm={stop}>
            <Button icon={<PauseOutlined />}>停止</Button>
          </Popconfirm>}
      <Button icon={<ReloadOutlined />} onClick={load}>刷新</Button>
    </Space>
    {service && (
      <Descriptions className="mt-4" size="small" column={3} items={[
        { key: 'st', label: '服务状态', children: <Tag color={service.status === 'Running' ? 'green' : 'red'}>{service.status}</Tag> },
        { key: 'srt', label: '启动类型', children: service.startType || '-' },
        { key: 'sn', label: '服务名', children: service.name },
      ]} />
    )}
    <div className="mt-3 text-xs text-fog">{footer}</div>
  </div>
</Spin>
```

### 2.5 六个面板各消费哪一部分、各自保留哪些协议特有逻辑

| 面板 | 消费的共享部分 | **必须保留**的协议特有部分 |
| --- | --- | --- |
| `NfsPermPanel` | `useProtocolPermissions({ protocol:'nfs', shareName: share.name, toRow, toPerm, rowKey:'clientName' })` + `ProtocolPermissionShell` | `NfsClientPerm` 行类型（`clientName/permission/type`）；`toClientPerm`（`Full\|Change → rw`，`deny → Deny`）；`toSharePerm(row, shareName)`（`accountType:'Group'`、`rw → Change`）；`PERMISSION_OPTIONS`（只读 (ro)/读写 (rw)）；`TYPE_OPTIONS`（允许/拒绝）；列定义（客户端/权限 140/类型 110/移除 50）；`rowKey="clientName"`；空态「暂无客户端规则」；头部说明（含 `<code>*</code>`、`192.168.1.0/24`）；添加区（`Input` 240px + 权限 + 类型 + 添加）与占位符「客户端名称（如 * 或 192.168.1.0/24）」；保存确认「确认覆盖当前 NFS 客户端权限？」；保存成功文案「权限已保存」；提示句（`<Tag color="purple">NFS</Tag>` + 拒绝优先/未匹配遵循共享默认） |
| `FtpPermPanel` | 同上，`protocol:'ftp'`、`rowKey:'account'` | `FtpRule`（`account/accountType/perm/type`）；`toRule`（`Change\|Full → rw`）；`toSharePerm(row, shareName)`（保留 `accountType`）；`PERM_OPTIONS`（只读 (Read)/读写 (Read, Write)）；`TYPE_OPTIONS`；**用户/组 Tag 列**（`blue`/`purple`，「用户」/「组」，宽 90）；权限列宽 140；空态「暂无授权规则」；头部说明；添加区含**用户/组选择**（宽 90）与占位符「账号名（如 * 或 Administrators）」；保存确认「确认覆盖当前 FTP 授权规则？」；保存成功「授权规则已保存」；提示句（`<Tag color="green">FTP</Tag>` + roles/users、拒绝优先） |
| `WebdavPermPanel` | 同上，`protocol:'webdav'`、`rowKey:'account'` | `WebdavRule`（`account/accountType/perm`，**无 type/deny**）；`toRule`（`Full→full`、`Change→rw`、否则 `ro`）；`toSharePerm(row, shareName)`（`full→Full`、`rw→Change`、`ro→Read`，`deny:false`）；`PERM_OPTIONS` 三档（只读 (Read)/读写 (Read, Write)/完全 (Read, Write, Source)）；**无「授权/Deny」列**；权限列宽 200；用户/组 Tag 列；空态「暂无作者规则」；头部说明；添加区含用户/组选择 + 三档权限；保存确认「确认覆盖当前 WebDAV 作者规则？」；保存成功「作者规则已保存」；提示句（`<Tag color="orange">WebDAV</Tag>` + Source 语义 + roles/users） |
| `NfsSettingsPanel` | `useProtocolSettings({ protocol:'nfs', api: { getConfig: api.nfs.getConfig, … }, texts })` + `ProtocolSettingsShell` | **降级分支（保留在面板内）**：`installed === false` → `<div className="glass-card p-4"><ProtocolCapabilityBanner protocol="nfs" /></div>`；`installed === null` → `<div className="glass-card p-4"><Spin tip="正在检测 NFS 协议..." /></div>`；`NfsServerConfig` 全部字段布局（基础配置 4 个 Switch；「连接与超时」Collapse 5 个 InputNumber；「身份映射 / 网关信息（只读）」4 个 disabled 字段）；`NFS_CONFIG_PRESETS`；确认文案「确认恢复 NFS 默认配置？」「重启 NfsService 服务？」「停止 NfsService 服务？」；服务成功文案「NFS 服务已重启/已启动/已停止」；底部说明（NFS 即时生效/部分需重启/客户端能力检测见共享管理页） |
| `FtpSettingsPanel` | 同上，`protocol:'ftp'` | 降级分支（`ProtocolCapabilityBanner protocol="ftp"` + 「正在检测 FTP 协议...」）；**三组协议特有下拉选项** `SSL_POLICY_OPTIONS` / `ISOLATION_OPTIONS` / `LOG_PERIOD_OPTIONS`；`FtpServerConfig` 字段布局（SSL 5 项 / 认证 3 项 / 被动端口 2 项；Collapse「消息与目录浏览」6 项、「用户隔离 / 超时 / 文件处理 / 日志」8 项）；`FTP_CONFIG_PRESETS`；确认文案「确认恢复 FTP 默认配置？/ 重启 ftpsvc 服务？/ 停止 ftpsvc 服务？」；服务成功文案；底部说明（IIS ftpServer/* 配置节、站点级配置在共享管理页、IIS 锁定可能写入失败） |
| `WebdavSettingsPanel` | 同上，`protocol:'webdav'` | 降级分支（`ProtocolCapabilityBanner protocol="webdav"` + 「正在检测 WebDAV 协议...」）；`WebdavServerConfig` 字段布局（含 `form.setFieldsValue` 之外的 `config` 派生值——保留 `const config: Partial<WebdavServerConfig> = data?.c ?? {}` 这一既有派生，若面板确实用到）；`WEBDAV_CONFIG_PRESETS`；确认文案「确认恢复 WebDAV 默认配置？/ 重启 W3SVC 服务？/ 停止 W3SVC 服务？」（按现文本逐字）；服务成功文案；底部说明（IIS WebDAV 配置、Source 语义） |

> 表格里的行为契约以**现有源码文本**为准：实现时逐字搬运，不要"顺手改文案"。任何文案差异都会让 §3 的可见文本断言失败。

---

## 3. 回归矩阵

### 3.1 逐点映射

图例：**既有保护**＝已存在、本次只许保留不许弱化的用例（`文件 :: 用例名`）；**必须新增**＝本次要加的用例与断言（可放既有文件，也可放新建文件）；**新建文件**＝该点要求创建的测试文件。

| # 点 | 既有保护（不得删改） | 必须新增（写清断言） | 新建文件 |
| --- | --- | --- | --- |
| 1 `DiagnoseModal:158` | `DiagnoseModal.test.tsx :: 共享名下拉来自真实共享列表（api.adapter.list("smb")），且允许留空`；`:: 选定共享后诊断：run 收到该共享名，且换目标会清空旧结论`；`:: initialShareName 带入目标共享（从共享行点进来的路径）`；`:: run 抛错时整体错误可见（不白屏）并可重试；恢复后错误条消失` | ① 打开→**恰好 1 次** `api.adapter.list('smb')`：`await waitFor(1 次)` 后再等 200ms 仍为 1（重复渲染不重发）；② 关闭再打开→再 1 次（合计 2），且上一次的结论（红绿灯行）、错误条、运行态**全部归零**（回到初始态）；③ `open` 保持 true 而 `initialShareName` 变化→结论清空、下拉选中项跟随；④ 列表拉取失败→仍可诊断（下拉为空、不弹阻断），与既有"不阻断全局体检"一致 | — |
| 2 `FirstRunGuide:52` | `FirstRunGuide.test.tsx :: 默认路径来自 api.disk.suggestRoot()`；`:: 创建失败：错误文案可见、不丢失已填内容、不写完成标记`；`:: 「以后再说」：只关闭；不创建、也不写标记（还没完成初始化）`；`:: 共享名默认由路径末级目录带出且可改` | ① 走到第 3 步并填写后关闭→再次 `open`→**回到第 1 步**，路径/共享名/权限档/高级参数/错误条全部为初始值（`setAdvanced({})` 语义：展开项不残留）；② 打开→**恰好 1 次** `api.disk.suggestRoot()`（一次打开内重复渲染不重发）；③ "手改过名字"标记被重置：改完名→关闭→重开→再换路径→共享名重新由路径末级带出；④ 创建失败后再重开，错误条清空 | — |
| 3 `GroupManageModal:63` | 无（该组件今日无测试） | ① 打开→**恰好 1 次** `api.user.list`；② 关闭再打开→成员列表/新增成员输入/重命名态（`renaming`/`newGroupName`）/批量选中项**全部回到初始**（描述字段仍来自 `group.description`）；③ 切换 `group`→成员与描述按新组重填，且 `api.user.list` 再 1 次；④ 保存描述失败→`message.error(原因)`、**不关窗**、不假成功；⑤ 用户列表拉取失败→静默（不弹错、批量添加区为空但主功能可用） | **`src/components/GroupManageModal.test.tsx`** |
| 4 `JournalDrawer:127` | `JournalDrawer.test.tsx :: 两类行渲染：新→旧列出，可撤销行给按钮、仅留档行只说明`；`:: 空态区分两种处境：从未有过操作 vs 筛选后无结果`；`:: 撤销成功：显示 undoJournal 返回文案并刷新列表`；`:: 撤销失败：透传原始错误文案，不吞错不假成功、不刷新`；`:: 清空记录：确认框写明条数与影响范围，确认后调用 clearJournal 且列表变空` | ① 打开→**恰好 1 次** `api.state.journalList`（重复渲染不重发）；② 关闭再打开→再 1 次（合计 2）；③ **stale-while-revalidate**：store 为空时打开→出现加载态（antd `Spin` 的 `aria-busy` 或 `.ant-spin-spinning`）；store 已有数据时打开→**不出现**加载态、立即渲染旧数据（断言首帧即可见某条记录文本）；④ `loadJournal` 失败→`message.error(原因)`，列表保持原样（不假成功、不置空）；⑤ 打开时恰好一次 IPC：`await waitFor(1)` 后静置 200ms 仍为 1 | — |
| 5 `PermissionDrawer:118` | 无（该组件今日无测试） | ① SMB 共享打开→`api.share.permissions` **恰好 1 次** + `api.user.list`/`api.user.groups` 各 1 次；② 重复渲染不重发；③ 关闭再打开→权限行重载（再各 1 次），**NTFS 页回到"未加载"初始态**（显示「点击「加载」查看 NTFS ACL（只读）」）；④ 切换 `share`→重载且以新共享数据为准（竞态断言：先让 A 的 `permissions` 慢、B 的快，最终显示 B 的行）；⑤ 加载失败→`message.error(原因)`、表格保持空态、不假成功；⑥ 本地加行后保存成功→`api.user.setSharePermissions` 1 次 + 重读 1 次 + `message.success`；⑦ 保存失败→`message.error(原因)`、**不重读**、本地编辑行仍在；⑧ 非 SMB 共享→渲染对应 PermPanel（`Nfs/Ftp/Webdav` 标题或空态文案可见），**不**调用 `api.share.permissions` | **`src/components/PermissionDrawer.test.tsx`** |
| 6 `PermissionMatrix:295` | `PermissionMatrix.test.tsx :: 导出报告(HTML) 与 CSV/JSON 并列，数据加载完成后启用`；`:: 点击导出：经同一 download 助手产出转义后的自包含 HTML 文件` | ① 挂载→`api.share.list`/`api.user.list`/`api.user.groups` 各 **1 次**，`api.share.permissions` 恰好 = 普通共享数（IPC/Special 被过滤，不计入）；② **加载中不出现空态**「暂无共享」（§1.6 披露 1）；③ 点「取消」→`message.info('已取消加载')`、按钮变回「重新加载」、取消后**不再新增** `api.share.permissions` 调用（`mapPool` 的 `shouldCancel` 生效）；④ 点「重新加载」→再各 1 次（总次数 +共享数），按钮在加载中变回「取消」；⑤ `api.share.list` 抛错→`message.error(原因)`，不出现假数据、导出按钮保持禁用 | — |
| 7 `FtpPermPanel:87` | 无（`src/components/PermissionPanel/` 今日零测试） | ① 挂载→`api.adapter.permissions('ftp', share.name)` **恰好 1 次**；② 重复渲染不重发；③ `share.name` 变化→再 1 次且行内容替换（竞态：新共享数据最终胜出）；④ 保存→`api.adapter.setPermissions('ftp', name, perms)` 参数逐项正确（`rw→Change`、`ro→Read`、`deny` 映射、`accountType` 保留）+ 重读 1 次 + `message.success('授权规则已保存')`；⑤ 保存失败→`message.error(原因)`、不重读、本地编辑不丢；⑥ 加载失败→`message.error(原因)`、空态仍是「暂无授权规则」；⑦ 协议特有列与文案：用户/组 Tag（「用户」「组」）、权限选项「只读 (Read)」「读写 (Read, Write)」、授权列（允许/拒绝） | **`src/components/PermissionPanel/FtpPermPanel.test.tsx`** |
| 8 `NfsPermPanel:84` | 同上（无） | ① 挂载→`api.adapter.permissions('nfs', share.name)` **恰好 1 次**；② 重复渲染不重发；③ `share.name` 变化→再 1 次且行替换；④ 保存→`setPermissions('nfs', name, perms)` 参数正确（`rw→Change`、`ro→Read`、`deny`、`accountType:'Group'`）+ 重读 + `message.success('权限已保存')`；⑤ 保存失败→不重读、编辑不丢；⑥ 加载失败→空态「暂无客户端规则」；⑦ 协议特有列/文案：`客户端` 列、权限选项「只读 (ro)」「读写 (rw)」、类型选项「允许」「拒绝」、头部含通配符说明 | **`src/components/PermissionPanel/NfsPermPanel.test.tsx`** |
| 9 `WebdavPermPanel:83` | 同上（无） | ① 挂载→`api.adapter.permissions('webdav', share.name)` **恰好 1 次**；② 重复渲染不重发；③ `share.name` 变化→再 1 次；④ 保存→`setPermissions('webdav', name, perms)` 参数正确（`full→Full`、`rw→Change`、`ro→Read`、`deny:false`）+ 重读 + `message.success('作者规则已保存')`；⑤ 保存失败→不重读、编辑不丢；⑥ 加载失败→空态「暂无作者规则」；⑦ 协议特有：三档权限选项（含「完全 (Read, Write, Source)」）、**没有**「授权/允许-拒绝」列 | **`src/components/PermissionPanel/WebdavPermPanel.test.tsx`** |
| 10 `PresetEditor:60` | 无（该组件今日无测试） | ① 打开→条目来自 `preset.entries` 的**深拷贝**（改一行后传入的 `preset` 对象不变）；② 关闭再打开→新增账号输入/类型/权限/拒绝开关回到初始（`''`/`User`/`Read`/`false`）；③ 切换 `preset`→条目替换为新预置；④ 打开→`api.user.list`/`api.user.groups` 各 **1 次**（候选账号；既有 effect 未改，作为"不许顺手重写"的看门用例）；⑤ 保存失败→`message.error(原因)`、不关窗、编辑不丢 | **`src/components/PresetEditor.test.tsx`** |
| 11 `ShareDetailDrawer:113` | 无（该组件今日无测试） | ① SMB 打开→`api.share.connections`/`api.share.openFiles` 各 **1 次**；② 重复渲染不重发；③ 关闭再打开→**tab 回到 `info`**，连接/打开文件再各 1 次；④ 切换 `share`→重载并重置 tab，新共享数据最终胜出；⑤ 非 SMB（nfs/ftp/webdav）→**不**调用 `connections`/`openFiles`（协议降级分支），且按协议预填表单字段；⑥ 连接加载失败→`message.error(原因)`；⑦ 打开文件加载失败→**静默**（不弹错，列表为空） | **`src/components/ShareDetailDrawer.test.tsx`** |
| 12 `UserCreateModal:45` | 无（该组件今日无测试） | ① 打开→`api.user.groups` **恰好 1 次** + 表单重置为默认勾选（启用/可改密码/密码永不过期）；② 关闭再打开→已选组、密码强度提示清空；③ 重复渲染不重发；④ 两次密码不一致→**不**调用 `api.user.create` 且 `message.error('两次输入的密码不一致')`；⑤ 创建失败→`message.error(原因)`、不清空已填、不关窗；⑥ 组列表拉取失败→静默（不弹错，创建仍可用） | **`src/components/UserCreateModal.test.tsx`** |
| 13 `UserDetailDrawer:82` | 无（该组件今日无测试） | ① 打开→tab 回到「属性」、密码区收起、新密码输入清空；② 切换 `user`→表单按新用户重填；③ 关闭再打开→未保存编辑不残留；④ 保存失败→`message.error(原因)`；⑤ Tab 切换触发的组/权限加载不受影响（每次切换 Tab 各 1 次，事件路径） | **`src/components/UserDetailDrawer.test.tsx`** |
| 14 `pages/Settings.tsx:1132` | `Settings.test.tsx :: 阈值没改动就不提交（避免无意义写入）`；`:: 四项都可编辑：每次只提交被改的那一项，其余项由浅合并保留`；`:: 保存失败：原因可见，开关回滚`；`:: 打开 Tab 后各读请求只发一次，静置 800ms 不自动重取` | ① **外部值变化→草稿跟随**：外部改 `alertRules.diskLowGb`（store 落盘）后，输入框显示新值；② 用户改动→`diskDirty` 为真、提交 1 次 `state.patch({ alertRules: { diskLowGb: N } })`；③ 保存失败→草稿退回**当前**服务端值（不残留脏值）且原因可见；④ 外部值变化与用户编辑交错：外部刷新不得打断"已改但未提交"的草稿（保持 dirty） | — |
| 15（R-5 三 SettingsPanel） | `Settings.test.tsx`（只覆盖到"设置页不产生轮询风暴"与其它卡片；协议面板因 `protocol.detect` 返回 `{}` 而只渲染降级分支，**未真正覆盖**） | ① 每面板：`installed === true` 时挂载→`getConfig`/`serviceStatus` 各 1 次（`enabled` 门控正确）；② `installed === false`→渲染 `ProtocolCapabilityBanner`（协议对应文案），**不**调用 `getConfig`；③ `installed === null`→渲染「正在检测 NFS/FTP/WebDAV 协议...」；④ 保存→`setConfig(表单值)` 1 次 + `message.success('已保存')` + 失效重取；⑤ **校验失败不弹 message**（`validateFields` 在 try 之外）；⑥ `restart`/`start`/`stop`→对应 IPC 1 次 + 协议特有成功文案 + 重取；服务状态为 `Stopped` 时显示「启动」而非「停止」；⑦ `restoreDefault`→IPC 1 次 + `message.success('已恢复默认配置')` + 表单被填入默认值 + 重取；⑧ 各失败路径→`message.error(原因)` 且不重取；⑨ 刷新 tick（`uiStore.refreshTick` 变化）→仅在已安装时重取 1 次；⑩ 协议特有字段/确认文案逐字可见（§2.5 表） | **`src/components/ProtocolSettingsPanels.test.tsx`** |

### 3.2 必须新建的测试文件（清单）

| 文件 | 归属 | 覆盖点 |
| --- | --- | --- |
| `src/components/PermissionPanel/NfsPermPanel.test.tsx` | t2（inScope 内） | 8 |
| `src/components/PermissionPanel/FtpPermPanel.test.tsx` | t2（inScope 内） | 7 |
| `src/components/PermissionPanel/WebdavPermPanel.test.tsx` | t2（inScope 内） | 9 |
| `src/components/PermissionDrawer.test.tsx` | t3（inScope 内） | 5 |
| `src/components/GroupManageModal.test.tsx` | t3（inScope 内） | 3 |
| `src/components/PresetEditor.test.tsx` | t3（inScope 内） | 10 |
| `src/components/ShareDetailDrawer.test.tsx` | t3（inScope 内） | 11 |
| `src/components/UserCreateModal.test.tsx` | t3（inScope 内） | 12 |
| `src/components/UserDetailDrawer.test.tsx` | t3（inScope 内） | 13 |
| `src/components/ProtocolSettingsPanels.test.tsx` | **panels-dev（需 captain 扩 t2 inScope）** | 15 |

### 3.3 断言口径（可被实现者直接照做）

1. **"只加载一次"必须用次数断言 + 静置**：`await waitFor(() => expect(mock).toHaveBeenCalledTimes(1))`，随后 `await new Promise((r) => setTimeout(r, 200))` 再断言仍为 1（对齐 `Settings.test.tsx :: 打开 Tab 后各读请求只发一次，静置 800ms 不自动重取` 的既有手法）。断言"次数"，不断言内部 state。
2. **"切换目标→重载"要断 IPC 次数 + 数据内容**，并在竞态用例里让旧目标响应**晚于**新目标返回，断言最终显示新目标数据（§1.6 披露 2）。
3. **"关闭再打开→回到初始态"**：断言**可见文本/控件值**（输入框值为空、tab 选中项、错误条消失、结论区回到初始文案），不断言内部 state。
4. **"失败→原因可见且不假成功"**：mock 抛 `new Error('...')`，断言 `message.error` 文案含该原因；同时断言**不该发生的事没发生**（不重读、不关窗、不改数据、不显示成功文案）。
5. **"协议不支持→降级分支"**：断言降级文案/组件可见，并断言**不**发生该协议专属 IPC。
6. **"编辑态/草稿不丢"**：改一行（或改输入）→ 触发一次失败保存 → 断言编辑仍在；再断言 `setPermissions`/`setConfig` 未被以"成功"文案回报。
7. **IPC 桩**：沿用现有手法（`src/api.ts` 在模块加载时读 `window.winshare`，必须在 import 组件前注入 stub；`vi.hoisted` 建桩；`App` + `QueryClientProvider(retry:0)` 包裹）。三 SettingsPanel 的用例需要 stub `uiStore.protocolCaps`（`installed` 三态各一组）。
8. **不得删改既有用例**：§3.1 的"既有保护"列只许保留；新增用例放在对应 `describe` 内（或新建文件），不要为了通过而放宽既有断言。

### 3.4 预期零改动的文件（跑偏信号）

本规格的 A/B/C 三类方案**都不需要修改任何父组件的挂载方式**（不需要 `key` 重挂载、不需要父组件传 `open` 之外的额外 props）。因此：

- `src/pages/Shares.tsx`、`src/components/SharesWithOps.tsx`、`src/App.tsx` 预期**零改动**（t3 的 inScope 虽然包含前两者，但本规格的落地方案用不到）。
- `src/components/PermissionDrawer.tsx` 预期**零改动**（§2.0 边界 1）。
- 若实现中发现必须改这些文件才能消除违例，说明方案跑偏：**停下来报 captain**，不要自行扩大改动面。

---

## 4. 不变式与非目标

### 4.1 对外行为零变化（硬性）

1. **IPC 调用次数与参数不变**：打开/切换目标各恰好一次；保存成功 = 保存 1 次 + 重读 1 次；保存/加载失败不产生额外请求；不引入缓存层（这是否决 react-query 的首要原因，§1.4）。
2. **文案逐字不变**：按钮、空态、确认框标题、成功/失败提示、头部/底部说明、占位符、Tab 标题。
3. **空态与降级分支不变**：PermissionMatrix「暂无共享」；三 PermPanel 的「暂无客户端规则/暂无授权规则/暂无作者规则」；三 SettingsPanel 的 `ProtocolCapabilityBanner` 与「正在检测 … 协议...」；ShareDetailDrawer 非 SMB 分支；PermissionDrawer 非 SMB 走 PermPanel。
4. **权限模型映射不变**：Nfs `ro/rw + Allow/Deny → Read/Change + deny`、Ftp 同上且保留 `accountType`、Webdav `ro/rw/full → Read/Change/Full` 且 `deny:false`。
5. **列定义、宽度、`rowKey`、tab 默认值、表单默认勾选不变**。
6. 允许的收紧**只有** §1.6 的两条，且必须在交付说明与评审里显式声明。

### 4.2 禁止夹带（本批次非目标）

暗色模式双轨接线、英文 i18n、应用内检查更新、设置整体导入导出、预约式任务、真机端到端（真实 SMB/IIS/防火墙/icacls/开机自启/跨小时趋势）。**一律排除**：不得在本批次 diff 中出现相关代码、配置、文案或依赖。

### 4.3 禁止的规避手法

§0.4 红线 1-5：延时/微任务藏 setState；`useLayoutEffect` 换壳；把 state 搬进 ref 静音；`set-state-in-effect` 行内 disable；把规则退回 off。reviewer（t5/t6）与 verifier（t4）须逐文件检查这五条。

---

## 5. 交付与交接

### 5.1 本规格不做什么

不改任何 `src/**`、`electron/**` 代码；不改 `eslint.config.mjs`、`CHANGELOG.md`、`package.json`、`vitest.config.ts`。规则复启（`'react-hooks/set-state-in-effect': 'off'` → `'error'`）与留档注释纠正是 **t7** 的动作。

### 5.2 给 captain 的两项待决（阻塞下游，需在派活前处理）

1. **verify 命令勘误（§0.3）——captain 已处理**：`--format unix` 在本机不可用（`eslint-formatter-unix` 未安装、`package.json` 在 outOfScope），以 exit 2 失败且与代码状态无关。影响面只有 **t1 与 t4**（t7 的 verify 是纯 pnpm 门禁，从不含该参数；t2/t3 也不含）；captain 已把 t4 的 verify 改为 `--format json`。
2. **R-5「三 SettingsPanel」半边无归属（§2.0 边界 2）**：t2 只覆盖 `PermissionPanel/**` + `useProtocolPermissions.ts`，t3 覆盖 modal/drawer + Settings.tsx，三 SettingsPanel 的抽象**无人认领**。本规格已把 3 个追加文件（`useProtocolSettings.ts`、`ProtocolPermissionShell.tsx`、`ProtocolSettingsShell.tsx`）与 `ProtocolSettingsPanels.test.tsx` 明确划给 **panels-dev**；captain 需要按 §2.0 边界 2 的清单扩展 t2 的 inScope（或另开 owner 明确的实现任务）。在扩展之前，t2 无法完整交付 R-5。

### 5.3 证据留档（本机实测，原始输出摘要）

```
# 起点违例（等价命令，--format unix 不可用见 §0.3）
$ node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}'
exit=1 ；14 条 react-hooks/set-state-in-effect：
src\components\DiagnoseModal.tsx:158:5
src\components\FirstRunGuide.tsx:52:5
src\components\GroupManageModal.tsx:63:7
src\components\JournalDrawer.tsx:127:5
src\components\PermissionDrawer.tsx:118:7
src\components\PermissionMatrix.tsx:295:5
src\components\PermissionPanel\FtpPermPanel.tsx:87:5
src\components\PermissionPanel\NfsPermPanel.tsx:84:5
src\components\PermissionPanel\WebdavPermPanel.tsx:83:5
src\components\PresetEditor.tsx:60:7
src\components\ShareDetailDrawer.tsx:113:5
src\components\UserCreateModal.tsx:45:7
src\components\UserDetailDrawer.tsx:82:5
src\pages\Settings.tsx:1132:5

# 契约里的 --format unix
$ node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}' --format unix
node.exe : The unix formatter is no longer part of core ESLint. Install it manually with
`npm install -D eslint-formatter-unix`
exit=2

# 规则行为探针（一次性文件，测完已删）
#   P-1 一 effect 只报第一处；P-2 一组件多 effect 各报一次；P-3 组件体 loader 一律报 / effect 内 loader 不报
#   P-4 ref 守卫不报；P-5 渲染期调整不报；P-6 useLayoutEffect 同样报；P-7 布尔守卫仍报
```

---

## 6. 本次交付未触碰源码（验收项）

`git status --short` 仅显示本交付文档 `?? docs/audit/05-renderer-debt.md`（未跟踪的新建文件，属 `docs/**`，由 t1 的 inScope 明确授权，在本批次实现任务的 outOfScope 之外）。`src/**`、`electron/**`、`eslint.config.mjs`、`CHANGELOG.md`、`package.json`、`vitest.config.ts` **均未修改**。取证方式：

```powershell
git status --short          # 期望：仅 docs/audit/05-renderer-debt.md（或为空，若已提交）
git status --short -- src electron   # 期望：空输出
```

---

## 7. 批次收口（renderer-debt 终态 · 由集成任务 t7 追加）

> §0-§6 是**施工规格与其起点取证**（t1 交付，2026-11 批 2）。本节是**批次终态收口**：交付清单、独立验证全部轮次结论、两轮评审结论、规格自身缺陷、遗留边界与诚实声明。凡与前三节冲突之处，**以本节为准**（前三节是"当时写的规格"，本节是"实际发生的事实"）。

### 7.1 交付清单

| 任务 | 归属 | 交付内容 |
| --- | --- | --- |
| t1 | analyst | 本文件（规格）：14 处违例逐点归档 + A/B/C 三类方案 + R-5 共享抽象 API + 回归矩阵。attempt 1 被上游 **429 限流**中断而记为 failed（**不是验收失败**），attempt 2 恢复状态、只做 3 处一行对一行的勘误 |
| t2 | panels-dev | **R-5 两半**：三 PermPanel → `useProtocolPermissions` + `ProtocolPermissionShell`；三 SettingsPanel → `useProtocolSettings` + `ProtocolSettingsShell`；三 PermPanel 的 effect-setState 违例归零。新增源码 5 个（两 hook + 两 Shell + `useResetOnOpen`）、新增测试 4 文件 **59 例**（NfsPermPanel 8 / FtpPermPanel 8 / WebdavPermPanel 8 / ProtocolSettingsPanels 35） |
| t3 | modals-dev | **modal/drawer 层 11 处迁移**：DiagnoseModal / FirstRunGuide / GroupManageModal / JournalDrawer / PermissionDrawer / PermissionMatrix / PresetEditor / ShareDetailDrawer / UserCreateModal / UserDetailDrawer / pages/Settings（派生态）。改 17 个文件、新建测试 6 文件 **32 例**（GroupManageModal 5 / PermissionDrawer 8 / PresetEditor 5 / ShareDetailDrawer 5 / UserCreateModal 5 / UserDetailDrawer 4），并在 6 个既有测试文件内新增用例（DiagnoseModal / FirstRunGuide / JournalDrawer / PermissionMatrix / Settings / Shares） |
| t8 | modals-dev | 修复 **F1**：`PermissionDrawer` 把「NTFS 归零键」与「带 nonce 的 loading 键」拆开（NTFS 键**不带** nonce，条件逐字沿用 HEAD 的 `open && share && protocol==='smb'`） |
| t9 | panels-dev | 修复 **F2**：`ProtocolSettingsPanels.test.tsx` 增加行内 `vi.setConfig({ testTimeout: 30_000 })` |
| t11 | panels-dev | 测试超时基线：`vitest.config.ts` 两个 project 各加 `testTimeout: 30_000`（**放宽等待窗口**，见 §7.6） |
| t13 | panels-dev | 修复 **F4**：`src/test/setup.ts` DOM 守卫内全局 `configure({ asyncUtilTimeout: 10_000 })`（**放宽等待窗口**，见 §7.6） |
| t4 / t10 / t12 | verifier | 独立验证 round 1（**红**）/ round 2（**红**）/ round 3（**绿**），取证件 `src/test/renderer-debt.verification.test.tsx`（终态 **18 例**） |
| t5 / t6 | reviewer | 两轮评审：R-5 抽象与协议权限面板（t2）、modal/drawer 11 处迁移（t3）——**均 verdict = pass、无 findings** |
| t7 | integrator | 本次集成：`eslint.config.mjs` 规则复启为 `error` + 留档注释重写；`CHANGELOG.md` 批次条目与「未完成 / 已知边界」纠正；`docs/audit/00-backlog.md` §3.5、`docs/audit/02-renderer.md` 状态收口；本文件 §7；终态六门禁取证 |

**终态规模**：47 个测试文件 / **650 个用例**；本批次新建 10 个测试文件共 **91 例**（t2 59 + t3 32），另有验证探针 18 例。工作区 `git status` 终态 46 条（29 改 + 17 新），其中 t7 的动作是 **4 条既有跟踪文件的改动**（`eslint.config.mjs` / `CHANGELOG.md` / `docs/audit/00-backlog.md` / `docs/audit/02-renderer.md`）+ **本文件（未跟踪）新增 §7**。

### 7.2 独立验证的完整轮次叙事（不得含糊）

| 轮次 | 任务 / attempt | 结论 | 关键证据 |
| --- | --- | --- | --- |
| round 1 | t4 attempt 1 | **红** | 抓到 **F1 行为回归**（「重新加载 / 保存后重读」清空已加载的 NTFS 只读视图）+ **F2 门禁不绿**（`pnpm test:coverage` 超时） |
| round 2 | t10 attempt 1 | **红** | **F1 已闭环**（探针由 verifier 本人反转：反转前 exit 1 / `16 tests \| 1 failed`，失败原文即"ACL 被清空"——**这条失败本身就是 t8 修复生效的机器证明**；反转后 18/18）。**F2/F3 未闭环**：`pnpm test:coverage` 连续 2 次 exit 1、失败点每次都换文件（`FirstRunGuide.test.tsx:203` 实测 5154ms vs 5000ms；`JournalDrawer.test.tsx:140`）。**对照实验**：把验证者的探针文件移出仓库后重跑仍 exit 1，超时点漂移到本批次**未改动**的 `Sessions.test.tsx` → 根因系统性（22 个 `*.test.tsx` 中 13 个缺超时放宽 + `vitest.config.ts` 无全局 `testTimeout`） |
| round 3 | t12 attempt 1 | **绿** | `pnpm test:coverage` run1/run2 均 exit 0（88.03s / 90.00s）；精确匹配 runner 告警原文 `Test timed out in` 在四次运行中**均为 0**；round 2 漂移过的四个文件本轮全 ✓（FirstRunGuide 25.67/28.24s、JournalDrawer 26.80/30.12s、Sessions 5.07/5.13s、ProtocolSettingsPanels 54.84/57.26s）；探针 18/18；六门禁全绿 |
| 追加轮 | t4 attempt 2 | **红** | 新红项 **F4**：`pnpm test` 首跑 exit 1（`3 failed \| 647 passed`），三个失败全在本批次**新建**的三 PermPanel 测试的同一处「重复新增行 → 提示」断言（NfsPermPanel:251 / FtpPermPanel:243 / WebdavPermPanel:234）；根因是 RTL `asyncUtilTimeout` 默认 1s 过紧 |
| 追加轮 | t4 attempt 3 | **绿** | 9 条验收全部通过；含对 t13 修复的**独立审计**（`git diff` 纯新增 9 行、node project 无副作用、在自己的探针里注入审计块独立复现「10s 窗口跑满后仍转红」）与 `pnpm test` ×3 / `test:coverage` ×2 连续绿 |

**账面状态必须按事实表述（同时写明，不得只留后者）**：

- **t4**：attempt 1 抓到**真实回归**、attempt 2 抓到**真实 F4**、attempt 3 全绿 → `completed`。attempt 1/2 的 failed 是**当时的真实结论**，attempt 3 的 completed 只反映**修复后的当前状态**；两者并存。
- **t10**：round 2 的独立验证判定 **failed** —— 这是 **round 2 的真实结论**（F1 已闭环、F2/F3 未闭环），**不得**读成「代码验证不通过」，也**不得**写成「F2 修复失败」；F2/F3 随后由 t9+t11 消除、经 round 3（t12）复验通过。captain 另外交办了「对现树账面复跑」以便把 t10 的验收项登记为闭环——无论该重派是否完成，**round 2 的红是本批次的历史事实**。
- **t8 / t9**：均为 `completed`，但它们的 **attempt 1 的 failed 不是「修复失败」**——修复本体自首次交付起即成立（t8 的拆键、t9 的 `vi.setConfig`），failed 的**唯一**原因是它们自己的 `pnpm test` 门禁被 t4 的独立验证取证件 `src/test/renderer-debt.verification.test.tsx` 挡住（该文件按设计固定 F1 **修复前**的缺陷行为，且不在 t8/t9 的 inScope 内、实现者**无权也不应**修改）。captain 已**驳回**「把该文件加入实现者 inScope 代改」的方案（会破坏证据独立性），翻面交由 verifier 本人在 round 2 完成（该文件 L651 留档了「round 1 固定缺陷 / round 2 固定修复」的角色演进）。
- **t12**（round 3）：`completed`。**t1** 的 attempt 1 failed 是上游 429 限流中断，不是验收失败；**t2/t3/t5/t6/t11/t13**：`completed`。

### 7.3 规格自身的缺陷（如实留档：6 处写错 + 1 处自相矛盾）

**结论先行：以下均为「规格写错、实现按 §1.4 / §4.1 的意图修正」，不是「实现跑偏」；不得隐去，也不得记成实现的过错。**

1. **§1.3 点 6 被规则行为探针 P-3 推翻**（PermissionMatrix）。规格写「只删 `setLoading(true)` 即可」；实测「**在组件体内**定义、内部含 `setState` 的 async loader，即使 `setState` 全在 `await` 之后，从 effect 调用**仍被报**」（同形 loader 定义在 **effect 内**则零报）。字面照做会**留下违例**。实现改为「effect 内定义 loader + nonce」，是唯一合规解。
2. **§1.3 点 2 被 P-4 推翻**（FirstRunGuide）。把 `nameEdited` 由 ref 改 **state** 会**新造违例**（`if (!nameEdited) setName(path)` 被报，58:22），且必须改动 §1.5 第 6 条**点名禁改**的 74-76 effect。实现**保留 ref**、复位留在原 `[open]` effect（该 effect 逐字未动）。
3. **§1.3 点 11 会让「刷新连接」多发一次 `openFiles` IPC**（ShareDetailDrawer），违反 §4.1「IPC 调用次数与参数不变」。实现改为**双序号**（`connNonce` / `filesNonce`）；变异实验反证：把打开文件的 effect 依赖改回 `connNonce`，`openFiles` 被调 2 次而非 1 次。
4. **§1.3 点 5 的 reset 键设计导致行为回归**（PermissionDrawer）。规格要求把复位键写成 `` `${open}|${name}|${protocol}|${nonce}` `` 且 reset 内放 `setNtfs(null)` —— 把「打开 / 换共享才归零」的**语义键**与「重载序号」混进**同一个键**，必然**扩大 NTFS 归零的触发集合**；而 HEAD 的 `setNtfs(null)` 只挂在 `[open, share]` effect 内，重载按钮与保存后重读走 `loadPerms()` **从不触碰 NTFS**。→ 这是**实现引入的真实回归**（由 t4 round 1 的 F1 发现、t8 拆键修复），**按 §1.4/§4.1 意图修复，不得登记为 §1.6 的「允许的收紧」**（把行为回归写成文档登记等于用文档掩盖回归）。
5. **§1.6 / §3.3 的判据「加载中不得出现空态文案」被否证**：实测 antd Table 的 `locale.emptyText` 在 `Spin` 期间**仍在 DOM**，该判据无法区分加载态与空态。已改为**可观测判据**（`Spin` 的 `aria-busy` / `.ant-spin-spinning` 是否存在、首帧是否已渲染旧数据）。
6. **§1.3 点 6/2/11 之外的另三处「规格描述与既有行为不符」**（t6 已读既有代码逐条确认，**均为规格写错**）：
   - **§3.1#4④「loadJournal 失败不置空」**：`src/stores/appStore.ts` 的 `loadJournal` catch 为 `set({ journal: [] }); throw e`（该文件本批次**零改动**）→ 失败**必清空**。
   - **§3.1#6⑤「`share.list` 抛错 → `message.error`」**：PermissionMatrix 三类读各自 `.catch(() => [])` 兜底 → 实为**静默降级**。
   - **§3.1#14④ 草稿语义**：HEAD 的 effect 依赖 `[rules.diskLowGb]`，外部值一变即覆盖草稿 → 规格字面的「外部刷新不得打断已改未提交的草稿」与 HEAD 及规格自带实现矛盾（实现按既有语义断言「**值不变**的外部刷新不打断」）。
   三处按 §4.1「行为零变化」**以既有行为为准**。
7. **§3.4 与 §1.3#5 的规格内部冲突**：§3.4 把 `src/components/PermissionDrawer.tsx` 列为「**预期零改动**」（跑偏信号），而契约第 1 条的 11 个违例点明确包含 `PermissionDrawer:118`、§1.3 点 5 也把该文件指派给 t3 迁移。→ **规格内部冲突**。**按 §1.3#5 记**（该文件**确实迁移**，t3 的迁移合规），**不得**记成 t2 或 t3 的违规。

**F1 的归因与次因（t6 判定，不淡化）**：(1) **主因**是上述第 4 条的规格 reset 键设计；(2) **次因**是**实现者自检维度缺失**——t3 的自检覆盖了「两条收紧 + 三处规格偏离 + 三处既有行为不符」，但没有一步「**reset 键的触发集合相对 HEAD 是否被扩大 → 逐个消费点核对可观察后果**」；它忠实执行了错规格，反而把规格缺陷放大成真实回归。t3 交付说明中未声明该项，但 F1 是 **t4 round 1** 在 t3 交付**之后**才发现的，因此**不构成隐匿**，只说明「照规格执行 + 自检」不足以覆盖可观察行为回归——这正是独立验证存在的价值。(3) 现已闭环：t8 拆键修复、回归用例钉住（把 nonce 并回键即转红）、t10/t12 复验通过。(4) **reviewer 的改进建议**：把「**reset 键触发集合相对 HEAD 的等价性**」列为**后续同类迁移的强制自检项**。

### 7.4 两轮评审结论

**t5（评审 R-5 抽象与协议权限面板迁移，实现 t2）：verdict = pass**，无 findings，6 条观察（见 §7.5）。

- 11 条验收全通过；`pnpm typecheck` / `lint` / `format:check` / `test`（47 files / 650 tests）全 exit 0，另加契约外的 override eslint = exit 0（131 文件 / errors·warnings·fatals 全 0 / **该规则 suppressions = 0**）。
- **抽象真的减少了重复（机器可比）**：12 条 Shell 文案/标签多重集计数 **3 → 1**；六面板内 `useEffect|useQuery|invalidateQueries|queryKey|setLoading|setRows|setSaving` 命中 **0**；9 个 IPC 调用点收敛为 1 份；行数 PermPanel 216/235/210 → 175/192/171（−123）、SettingsPanel 274/340/297 → 154/218/182（−357），新增两 Shell + 两 hook 共 470 行 → **净 −69 行**；第四个协议可复用（共享层**无任何** `protocol === 'x'` 式分支）。
- **结构性变化 (a)（动作按钮组 / 服务状态 Descriptions / 底部说明由 Form 内部移为兄弟节点）独立判为「无行为变化，不构成 finding」**：antd Button `htmlType` 默认 `button`；`@rc-component/form` 的 `onSubmit` 仅 `preventDefault + stopPropagation + submit()`，而三 Form 未传 `onFinish` 且各有 ≥2 个阻止隐式提交的 input → **Enter 行为与提交语义不变**；被切断的 `FormProvider/FormContext/DisabledContext/SizeContext` 在三 Form（只传 `form` + `layout`）下**取值相同**；`.ant-form` 只加 reset（无 margin/padding/border），被移动节点各自显式设色与字号 → **计算样式与垂直节奏不变**；`<form>` 无 accessible name → 非 landmark，role/name/label 关联未变。
- **(b)「校验失败不弹 message」以 hook 级用例覆盖判为足够**：三面板无任何 `rules=`、UI 层无法构造校验失败；面板不暴露 form 实例，面板级用例只能更远地打桩内部，加规则又违反 §4.1。变异 1（把 `validateFields` 移入 try）与变异 1b（移入 try 但 catch 仍抛出）**均能让该用例转红** → 它钉的是「顺序」本身。
- 钉住性抽查 4 次变异，全部按 sha256 **逐字节还原**；**无第三条未声明的可观察变化**（esbuild JSX 折叠比对 HEAD 六面板 CJK 字面量 **216 → 216**，零丢失零新增）。

**t6（评审 modal/drawer 11 处迁移，实现 t3，含 t8 的 F1 修复）：verdict = pass**，无阻断性缺陷，5 条观察（见 §7.5）。

- **归属**：t3 的 changedPaths 24 条（17 改 + 7 新）**全在 inScope 内**；inScope 内未改的 `Shares.tsx` / `SharesWithOps.tsx` 与 inScope 外的 `App.tsx` **零改动**（§3.4 跑偏信号成立）。
- 11 处逐点判定（类别 A/B/C · 方案 · 语义正确性）逐条 ✓，其中三处规格偏离（§7.3 第 1/2/3 条）被判为**正确偏离**。
- **反规避红线零命中**：无 `setTimeout` / `queueMicrotask` / `requestAnimationFrame` / `Promise.resolve().then`、无 `useLayoutEffect` 换壳、无「跳过首次渲染」标志位、**本规则行内 disable 0 命中**（机读 suppressions = 0）；全仓仅 3 个 `useRef`（请求序号 / `nameEdited` / 取消标记），均不参与渲染 → 非「把 state 搬进 ref 静音」。
- **行为与文案未变（机读）**：11 个被迁移组件 HEAD vs 现值含 CJK 字面量 **603 → 603，零丢失、零新增**。
- 钉住性抽查 7 次变异（含 F1 回归用例、双序号、SWR 门控、草稿同步），全部按 sha256 逐字节还原；临时探针文件**全部删除**，工作区无 `__t6` / MUTATION 残留。

### 7.5 遗留边界（**两轮评审的全部观察**，不得只记其一、也不得丢弃）

**t5 的 6 条观察**：

- **O1** §3.1#7⑦ 中 Ftp 的「用户」Tag **仅由下拉选项间接断言**，数据行的「用户」Tag 未直接断言 —— **测试覆盖的可选补强，本批次未做**。
- **O2** §3.1#15⑧ **未覆盖** `restoreDefault` / `start` / `stop` 的**失败路径**（同一 try/catch 形状）—— **这是测试覆盖的可选补强，本批次未做**；不得写成已修复，也不得写成不存在。
- **O3** §1.6#1「加载中不得出现空态文案」在**三 PermPanel 内无断言**（已由 t4/t12 探针的 `.ant-spin-spinning` + `aria-busy` 覆盖，§3.1 row 7/8/9 也未要求）。
- **O4** §3.4 与 §1.3#5 关于 PermissionDrawer 的**规格内部冲突**（见 §7.3 第 7 条）。
- **O5** hook 合并后**注释**去掉了协议专有措辞（如「避免未装时触发 `nfs:getConfig` 错误」→「`getConfig`」）—— 仅注释，行为不受影响。
- **O6** t2 的 changedPaths 使用了 `src/components/PermissionPanel/**` **通配**而非 6 条显式路径（harness 校验不展开目录通配）—— 仅可追溯性瑕疵。

**t6 的 5 条观察**：

- **O1** 三处规格写错 + §3.4 自相矛盾需在收口文档留档 —— 已由本节 §7.3 执行（§3.4 该行按 §1.3#5 记）。
- **O2** NTFS 归零键用 `name|protocol` 而非 `share` 对象标识：若父级在原共享上换一个「**同名新对象**」，HEAD 会复归零 NTFS、现实现不会；但**唯一生产调用点 `Shares.tsx:903` 把点击时的对象存进 state、打开期间标识不变**（仅点另一行才变名），故该差异**不可达**；11 个组件的 reset 键都属同一设计（§1.3 统一要求按 name 拼键），风险等价低。
- **O3** JournalDrawer 的 SWR 用例 `spinning()` 断言**对单点变异不敏感**（SWR 由「reset 条件」与「JSX 门控」双重保证，单拆任一都不改变可观察行为，双拆才失败）→ 它是**冗余保护而非空跑断言**；承载可见 SWR 的 `<Spin spinning={loading && sorted.length === 0}>` 与 HEAD 逐字相同。
- **O4** t3 的 changedPaths 逐条列真实文件（未用通配），**优于 t2 的做法**。
- **O5** hook 合并/迁移后的注释保留了原 `exhaustive-deps` 理由并新增触发面说明，未发现注释与行为不符（除 O2 的键语义表述可更精确）。
- **（t6 §七 的改进建议）** 把「**reset 键触发集合相对 HEAD 的等价性**」列为**后续同类迁移的强制自检项**。

**补充观察（t7 集成时逐文件复核，不影响任何既有 verdict）**：全仓 `react-hooks/exhaustive-deps` 行内豁免共 13 处，其中本批次新增 4 处——`PermissionDrawer:124`、`PermissionMatrix:307`、`ShareDetailDrawer:152`、`useProtocolSettings:109` **均附具体理由**；但同样由本批次新增的 `src/hooks/useProtocolPermissions.ts:99` 的豁免**未附理由**（既有的 `src/hooks/useEnsureProtocolCaps.ts:41` 同样未附理由）。这是**注释/可追溯性层面的可选补强，本批次未做**（不改变任何行为，也不构成 finding）；全仓 `set-state-in-effect` 行内 disable 仍为 **0**。

### 7.6 测试基建两处放宽的定性 + `setup.ts` 注释澄清

**t11（`vitest.config.ts`，panels-dev）** —— 改动：node project 第 21 行、dom project 第 32 行各加 `testTimeout: 30_000`（**纯新增 8 行**，含 6 行注释）；`coverage.thresholds`（lines 32 / statements 30 / functions 19 / branches 25）与 `include`/`exclude` **逐字未动**；**无并发相关键**（`maxWorkers`/`poolOptions`/`fileParallelism`/`isolate`/`sequence` 均未动）。

> 【如实定性（不得误读）】这是**放宽测试超时阈值（5s → 30s）以消除基建抖动**，**不是**修好了某个慢测试，也**不是**性能改进：没有加速任何用例、没有删改任何断言或等待、没有动被测路径。选全局而不逐文件补齐的理由：22 个 `*.test.tsx` 中 13 个无文件级 `vi.setConfig`；超时文件随负载**漂移**（FirstRunGuide / JournalDrawer / ProtocolSettingsPanels 各自在不同轮次超时），逐点加时覆盖不了漂移面；project 级设置对后续新增重型测试自动生效；数值与仓库既有 9 个文件级 `30_000` 取同一口径。

**t13（`src/test/setup.ts`，panels-dev）** —— 改动：DOM 守卫内全局 `configure({ asyncUtilTimeout: 10_000 })`（**纯新增 9 行**）；三 PermPanel 测试文件**一行未改**。

> 【如实定性】同样是**放宽 RTL 的异步等待窗口（1s → 10s）以消除基建抖动**，不是修好了慢测试、不是性能改进。它与 t11 是**两个不同的超时**：`vitest.config.ts` 的 `testTimeout` 管**单个用例的总时长**，RTL 的 `asyncUtilTimeout` 管 **`waitFor` 自身的异步轮询窗口**，**后者不被前者覆盖** —— 这正是 **F4 未被 t11 覆盖**的原因（两者已同步写进 `src/test/setup.ts` 注释）。
> 【代价与反证】真失败的 `waitFor` 现在最多烧 **~10s** 才报错（t13 自报 **10411ms**；verifier 在自己探针里独立复现 `AUDIT_ELAPSED_MS=10009`、失败用例 **10025ms** 仍转红）→ **只推迟暴露时点、未掩盖失败**。
> 【注释澄清项 · 行为不受影响】t13 注释里「与 `@testing-library/react` 共用同一实例（实测解析到同一路径）」这一**论证方式不准确**：verifier 实测 `AUDIT_SAME_CONFIG_OBJ=false`（`getConfig()` 对象标识不同）。但**行为结论正确且已独立复现**（RTL 的 `waitFor` 实测窗口 10011/10025ms、失败仍转红）。**建议表述**改为：「以 RTL `waitFor` 的实测超时窗口为准／RTL 的 `waitFor` 最终委托给 `@testing-library/dom` 的 `waitFor`，故 `configure` 到 dom 包生效」。本项属**注释澄清，行为不受影响** —— **不得**读成「t13 的修复有问题」。

### 7.7 既有的 stress 抖动（如实留档 · 未根治）

- **现象**：`electron/lib/powershellPool.stress.test.ts`（「场景2：100 并发含 10 条卡死命令」`:157`）间歇失败，原文 `expected [Array(82)] to have a length of 90`，带 retry x1 仍失败（retry 再次 76）。
- **定性**：**既有的负载敏感压力用例、本批次零改动、不是超时、是断言失败**。它**既不受 t11 也不受 t13 影响** —— 两者管的都是「等得不够久」，而它是「**结果不对**」。
- **本批次多次独立观测**：round 1、t12（round 3）、t6、t13 的并发窗口内（两次）均记录到；隔离跑 3/3 通过（3.05s）。
- **处置**：**未根治，建议另开专项**；`82 vs 90` **可能不只是环境抖动、也可能是进程池并发上限的真实边界信号**。
- **本任务 t7 的终态门禁未撞上该抖动**（`pnpm test` exit 0、`Test timed out in` = 0、无 `FAIL ` 行），因此本次**不需要**「首跑红 + 重跑绿」留档；历史轮次的红已在各自任务的 evidence_note 中如实保留，**未记为绿、也未隐瞒**。

### 7.8 终态六门禁（本任务 t7 实测，逐条退出码）

| 命令 | 退出码 | 输出摘要（本次实测） |
| --- | --- | --- |
| `pnpm typecheck` | **0** | `typecheck:node` + `typecheck:web` 两套 `tsc --noEmit` 均零错误（9s） |
| `pnpm lint` | **0** | `eslint . --max-warnings=0` 无任何输出（9s）。**规则已复启为 error 且全库零违例** |
| `pnpm format:check` | **0** | `All matched files use Prettier code style!`（4s） |
| `pnpm test` | **0** | `Test Files 47 passed (47)` / `Tests 650 passed (650)`；`Test timed out in` **0** 次；无 `FAIL ` 行（70s） |
| `pnpm test:coverage` | **0** | 47/650 passed；`All files` **42.02 / 40.81 / 30.7 / 44.09**（Stmts/Branch/Funcs/Lines），**均高于未改动的棘轮阈值 30/25/19/32**；无 `ERROR: Coverage`；超时告警 **0**（95s） |
| `pnpm build` | **0** | `electron-vite build` 三产物成功：`out/main/index.js` 221.31 kB、`out/preload/index.js` 11.50 kB、`out/renderer/assets/index-*.js` 6,247.37 kB（57s） |

**规则复启取证（Select-String + 规则级机读）**：

- `Select-String -Path eslint.config.mjs -Pattern "set-state-in-effect"` → 第 50 行的留档注释 + 第 63 行 `'react-hooks/set-state-in-effect': 'error',`
- `node_modules/.bin/eslint src electron --rule '{"react-hooks/set-state-in-effect":"error"}'` → **exit 0、零输出**（起点为 exit 1 / 恰好 14 条，见 §5.3）
- 取证**一律不使用** `--format unix` / `--format tap`：两者都已不在 core、必然 **exit 2**（见 §0.3 与 captain 的勘误）。

### 7.9 本任务的改动面与未做之事

- t7 只改 **5 个文件**（全在 inScope 内）：`eslint.config.mjs`（1 file / 14 insertions(+), 7 deletions(-)）、`CHANGELOG.md`、`docs/audit/00-backlog.md`、`docs/audit/02-renderer.md`、本文件。
- **未改**任何 `src/**`、`electron/**`、`package.json`、`vitest.config.ts`；**未回退规则**。规则复启后全库零违例，因此本任务契约第 4 条的「若仍有违例则如实报违例清单并让任务失败」**未触发**（不存在需要回退规则或就地改 src 的情形）。

### 7.10 诚实声明（非目标与证据边界）

- 本批次**未做**（仍如实保留为未完成）：**暗色模式双轨接线、英文 i18n 抽取、应用内检查更新、设置整体导入导出、预约式任务、真机端到端（真实 SMB / IIS / 防火墙 / icacls / 开机自启 / 跨小时趋势）**。
- 本批次全部验证证据**只到 jsdom + 门禁级**：IPC 全为 mock，未做真机 GUI 走查 → **不构成端到端验证**；本节及 §7.2 的任何「绿」都不得被表述为真机端到端通过。
- 独立验证的前两轮（round 1 / round 2）与 t4 attempt 2 **是红的**：本批次**不得**被概括为「六门禁全程全绿」；绿是**终态**的结论，不是每一轮的结论。
- F2/F3 的关闭依赖的是**放宽等待窗口**（t11），**不是**「修好了某个慢用例」；`powershellPool.stress.test.ts` 的断言抖动**未根治**，只是本批次终态未再触发。
- R-8 作为「数据获取模式重构」整体条目**仍为开放项**：本批次只完成了它的阻塞条件（规则复启）。

