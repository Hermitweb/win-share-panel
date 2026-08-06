# WinShare Panel 阶段 2–6 继续开发实施计划

## Context（背景与目标）

`docs/DEVELOPMENT.md` 6 阶段计划中，阶段 1（项目骨架 + PowerShell 执行器 + 审计 + 窗口风格化）已基本完成，阶段 2 核心完成，阶段 3–6 仅部分实现。本计划补齐用户确认的四方向缺口：

- **方向 A**（阶段 3）：用户权限 UI（共享权限查看/修改、NTFS 查看、权限矩阵、矩阵导出）— **当前最大功能缺口**
- **方向 B**（阶段 2 增强）：编辑共享、批量操作、筛选搜索、Del/Ctrl+N/F5/Space 快捷键
- **方向 C**（阶段 4/5 韧性）：会话实时刷新、批量断开、托盘气泡告警、SMB 配置快照/回滚、健康检查顶栏告警、仪表板报表导出
- **方向 D**（阶段 6）：跨域全局搜索 + 命令面板增强 + 完整快捷键体系

后端 IPC 已就绪 90%，主要工作量在 UI 层与若干韧性后端补齐。**所有改动保持浅蓝调磨砂玻璃风格与 antd v6 `items` 数组形式，不破坏既有 IPC 通道签名。**

## 设计原则

1. **状态中枢先行**：引入 Zustand（已在依赖中未使用）建 `src/stores/uiStore.ts`，集中路由、选中、刷新意图 tick、健康态、弹窗开关；hotkey 与页面通过 store 解耦。
2. **意图驱动热键**：`useHotkeys` 只写 store tick，对应页面订阅 tick 后在 `App.useApp()` 上下文执行 `modal.confirm` + API，保证消息上下文正确。
3. **危险操作二次确认**：行内 `Popconfirm`、批量用 `App.useApp().modal.confirm`（**严禁静态 `Modal.confirm`**，会丢失浅蓝主题）。
4. **类型四处同步**：每新增 IPC 必须 `electron/types.ts` → `src/types.ts`(re-export + WinShareApi 接口) → `electron/preload.ts` 同步。
5. **审计一致**：新增写操作 IPC 经 `wrap()` 包装；只读可不包装。

## 实施阶段（串行执行）

### 阶段 1：基础先行（状态中枢 + 热键 + 健康栏）

**新建文件**
- `src/stores/uiStore.ts` — Zustand store：`route`、`paletteOpen`、`shareCreateOpen`、`selectedShares: string[]`、`selectedSessions: string[]`、`refreshTick`、`shareDeleteTick`、`shareToggleTick`、`sessionCloseTick`、`health: HealthState | null`，及对应 setter/trigger。
- `src/hooks/useHotkeys.ts` — 全局 keydown：Ctrl+K→`setPaletteOpen(true)`、Esc→关闭所有弹窗、F5→`triggerRefresh`、Ctrl+N（仅 /shares）→`setShareCreateOpen(true)`、Del（仅 /shares 或 /sessions 且有选中）→对应 tick、Space（仅 /shares 且有选中）→`requestShareToggle`。**必须判定 `inField`（INPUT/TEXTAREA/contentEditable）屏蔽**，避免输入时误触。
- `src/components/HealthBar.tsx` — 30s 轮询 `api.system.health` + `api.smb.serviceStatus`，异常时渲染 antd `Alert banner`（红色，蜜桃粉强调），正常时 `return null` 不占位。
- `src/components/RouteSync.tsx` — `useLocation` 同步 `route` 到 store，置于 App 内（< 20 行）。
- 可选工具 hook `src/hooks/useTickEffect.ts` — 包装"跳过首次 tick"副作用，避免组件挂载误触发。

**修改文件**
- `src/App.tsx`：移除内联 Ctrl+K/Esc 监听，改 `useHotkeys()`；`paletteOpen`/`shareCreateOpen` 从 store 读；插入 `<RouteSync/>`。
- `src/components/Layout.tsx`：`<TitleBar/>` 与 body 间插入 `<HealthBar/>`。
- `src/components/CommandPalette.tsx`：`open` 改读 store `paletteOpen`。

**验证**：`pnpm dev` → Ctrl+K 开面板、Esc 关、F5 刷新、`Stop-Service LanmanServer` ≤30s 出现红色 HealthBar、恢复后消失、路由切换 store.route 实时变化。

### 阶段 2：阶段 3 权限 UI

**后端补齐** — `electron/services/user.ts` 的 `setSharePermissions`：当前只走 `Grant-SmbShareAccess`，**未处理 Deny**。改为：先 `Revoke-SmbShareAccess` 清授予 + `Unblock-SmbShareAccess` 清拒绝，再按 `perm.deny || access==='NoAccess'` 分流 `Block-SmbShareAccess`，否则 `Grant-SmbShareAccess`。`getSharePermissions` 已正确映射 `deny=AccessControlType===1`，无需改。Win10 1803 以下无 `Block-SmbShareAccess` 时 catch 降级为"仅 Revoke"并 audit failure。

**新建文件**
- `src/components/PermissionDrawer.tsx` — Props `{ open, share: Share|null, onClose }`；内部 antd `Drawer` + `Tabs items`：
  - 共享权限 Tab：拉 `api.share.permissions(share.name)` → 行内 `Select`（Full/Change/Read/Deny）编辑；底部"添加账号"行（`AutoComplete` 候选来自 `api.user.list` + `api.user.groups`）；保存用 `Popconfirm` → `api.user.setSharePermissions`，本地构造 perms 时把 Deny 项映射 `{access:'NoAccess', deny:true}`。
  - NTFS 权限 Tab：只读，`api.user.ntfsPermissions(share.path)` → `Table`（account/rights/type/inherited），`Tag` 标 Allow/Deny。
  - Drawer 面板用 `className` + `styles.body` 设磨砂玻璃背景（`rgba(255,255,255,0.75)` + `backdrop-filter: blur(16px)`）。
- `src/components/PermissionMatrix.tsx` — 行=共享、列=用户/组并集、单元格=`Tag`（Full=蓝/Change=紫/Read=灰/Deny=粉）。**N 次 PS 调用必须并发上限 4**（手写 Promise 池）；提供 loading skeleton + 取消按钮（用 ref 标志忽略过期结果）。Deny 优先显示（安全语义）。导出 CSV/JSON：`Blob` + `a.download`，沿用 [Shares.tsx#handleExport](file:///e:/workspace/win-share-panel/src/pages/Shares.tsx) 模式。

**修改文件**
- `src/pages/Shares.tsx`：操作列 `Space` 加"权限"按钮（icon `SafetyOutlined`）→ `setPermShare(r); setPermOpen(true)`；尾部渲染 `<PermissionDrawer/>`。
- `src/pages/Users.tsx`：`Tabs.items` 追加第三项 `{key:'matrix', label:'权限矩阵', children:<PermissionMatrix/>}`。

**验证**：点共享"权限"按钮 Drawer 滑出 → 改某账号为 Deny 保存 → 重开仍为 Deny → NTFS Tab 显示 ACL → Users 页权限矩阵加载完成、单元格颜色正确 → 导出 CSV 用 Excel 打开列对齐。

**风险**：共享 >20 时矩阵加载 >10s，必须并发上限 + 取消；长路径 NTFS 在 service 层加 `\\?\` 前缀。

### 阶段 3：阶段 2 共享管理增强

**修改文件**（仅 `src/pages/Shares.tsx`，重度改造）：
- **编辑 Modal**：复用 `Form`，新增 `editShare: Share|null` state；"编辑"按钮 `setEditShare(r); form.setFieldsValue({description:r.description}); setEditOpen(true)`；提交 `api.share.update(editShare.name, {description})`。**仅暴露 description**（`UpdateShareOpts` 只支持 description，后端 `Set-SmbShare -Description`）。
- **批量 rowSelection**：`selectedRowKeys` 绑定 store `selectedShares`；工具栏条件渲染"批量启停/批量删除"+ 已选计数；批量操作 `Promise.allSettled` 逐个执行，遇错收集后 `message.error` 汇总，最后 `load()` + `setSelectedShares([])`；用 `App.useApp().modal.confirm` 二次确认。
- **搜索过滤**：顶部 `Input`（prefix `SearchOutlined`），本地 `useMemo` 过滤 name/path/description；`Table dataSource={filtered}`。
- **hotkey tick 落地**：`useEffect` 订阅 store `shareCreateOpen`/`refreshTick`/`shareDeleteTick`/`shareToggleTick`；用 `useTickEffect` 跳过首次挂载误触发。

**验证**：搜索实时过滤 → 勾多行出现批量按钮 → 批量删除 `modal.confirm` 后逐个执行 → /shares 页 Ctrl+N 弹新建、Del 批量删除、Space 批量启停、F5 刷新 → 切其他页按 Ctrl+N 不弹（路由门控）。

**风险**：tick 初值触发需 ref 比较前后值；批量删除中途失败必须 `Promise.allSettled` 不阻断。

### 阶段 4：阶段 4/5 韧性

**修改 `electron/services/smb.ts`** — 新增快照/回滚：
```ts
export interface SmbSnapshot { id: string; ts: string; config: SmbServerConfig }
// setConfig 写前快照当前 config 到 %APPDATA%/WinSharePanel/smb-snapshots/<ISO>.json
//   ISO 时间戳作文件名需替换 : 为 -（Windows 不允许 :）
//   保留最多 20 份，超出按 ts 排序删旧
//   skipNextSnapshot 标志防止 rollback→setConfig 递归快照，finally 重置
// listSnapshots(): 返回 {id, ts}[] 按 ts desc
// rollbackSnapshot(id): 读快照 → skipNextSnapshot=true → setConfig(snap.config)
```

**修改 `electron/main.ts`** — `registerWindowIpc()` 内追加：
```ts
ipcMain.handle('window:balloon', (_e, title: string, body: string) => {
  tray?.displayBalloon({ title, content: body, iconType: 'info' })
})
```

**修改 `electron/ipc/index.ts`** — 注册 `smb:listSnapshots`（只读，可不经 wrap）、`smb:rollback`（写，经 wrap audit）。

**类型四处同步**：
- `electron/types.ts` 加 `SmbSnapshot`
- `electron/preload.ts` `smb` 命名空间加 `listSnapshots`/`rollback`；`window` 命名空间加 `showBalloon`
- `src/types.ts` re-export `SmbSnapshot`，`WinShareApi.smb` 加两个方法，`WinShareApi.window` 加 `showBalloon`

**修改 `src/pages/Sessions.tsx`** — 实时刷新 + 批量断开 + 气泡：
- `Select`（1/5/10/30s）+ 暂停按钮（`PauseOutlined`/`PlayCircleOutlined`）+ 倒计时 `Tag`
- `setInterval(load, 1000)` 倒计时归零触发 load；`prevUserRef` diff 新增会话 → `api.window.showBalloon('新 SMB 会话', ...)`；**仅在 `prevUserRef.size > 0`（非首次）触发**避免冷启动轰炸
- `rowSelection` 绑定 store `selectedSessions`；"批量断开"按钮 `modal.confirm` 后逐个 `api.session.close`
- 订阅 `sessionCloseTick`（hotkey Del）触发批量断开

**修改 `src/pages/Dashboard.tsx`** — 报表导出：
- `chartRef = useRef<ReactECharts>(null)`
- 导出 PNG：`chartRef.current?.getEchartsInstance().getDataURL({type:'png', pixelRatio:2, backgroundColor:'#F4FAFD'})` + `a.download`
- 导出 CSV：拼 `指标,值\n` 行（含 topShares）→ `Blob` 下载

**修改 `src/pages/Settings.tsx`** — 新增"快照历史"Tab：
- 拉 `api.smb.listSnapshots()` → `Table`（ts、操作列"回滚" `Popconfirm`）
- `fmtTs` 把 `2026-08-06T12-34-56-789Z` 解析为可读时间（`dayjs(id.replace(/(\d{4}-\d{2}-\d{2})-(\d{2})-(\d{2})-(\d{2}).*/, '$1 $2:$3:$4'))`）

**验证**：会话页选 5s 看倒计时 → 用别机 `net use \\本机\c$` 5s 内列表新增 + 托盘气泡 → 勾多会话批量断开 → /sessions 按 Del 触发批量断开 → Settings 改 SMB 配置保存 → 快照历史 Tab 看到新条目 → 回滚后配置恢复且**未产生新快照**（验证 `skipNextSnapshot`）→ 仪表板导出 PNG/CSV。

**风险**：递归快照必须 `skipNextSnapshot` 标志且 finally 重置；快照无限增长必须 cleanup 保留 20；Win10 `displayBalloon` 可能被通知中心静默，兜底 `mainWindow?.flashFrame(true)` + 获焦后 stop；会话气泡同用户 60s 内不重复（节流）。

### 阶段 5：阶段 6 收尾（全局搜索 + 命令面板增强）

**修改 `src/components/CommandPalette.tsx`**：
- `Command` 接口扩为 `{ key, label, hint, group: 'nav'|'share'|'user'|'session'|'action', action: () => void }`
- 输入 ≥2 字符时 200ms 防抖跨域拉 `api.share.list` + `api.user.list` + `api.session.list`（各 `catch(()=>[])` 兜底），各域取前 10 条
- 静态 nav + 动态结果按 `group` 分组渲染（自定义分组标题）
- 键盘上下选择（`ArrowUp/ArrowDown/Enter`，维护 `activeIndex`，回车执行 `action()` 后 `onClose`）
- 选中共享命令：`navigate('/shares')` + `uiStore.setSelectedShares([name])`，Shares 页表格 `rowSelection.selectedRowKeys` 跟随 store 高亮（v6 Table `scrollTo({key})` 滚动到该行）

**验证**：Ctrl+K 输入"ad" 200ms 后跨域结果分组显示 → 上下键导航 → 回车跳转 → 共享命令跳转后该行高亮选中。

**风险**：搜索频繁触发 PS 必须 200ms 防抖 + ≥2 字符；用闭包 `t` 变量比对忽略过期结果（避免慢响应覆盖快输入）；跨域搜索在非管理员环境 `api.session.list` 失败已 `catch(()=>[])` 兜底。

## 跨阶段通用注意事项

- **CSP 阻塞点**：`src/index.html` CSP 为 `default-src 'self'; img-src 'self' data:`；现有 `Shares.tsx` 已用 `URL.createObjectURL(blob)` + `a.click()` 下载且工作正常，说明 anchor 导航不受 default-src 限制。**阶段 4 PNG 导出用 `data:` URL 已在 img-src 白名单；CSV 用 `blob:` 同 Shares 现有模式，应无阻塞。若实测被拦，补 `default-src 'self' blob:`。**
- **类型同步清单**：每次后端新增 IPC，四处同步：`electron/types.ts` → `src/types.ts`(re-export + WinShareApi) → `electron/preload.ts` → `electron/ipc/index.ts`。
- **错误提示**：所有 `call()` 调用 try/catch → `message.error((e as Error).message)`，沿用 [Shares.tsx#handleDelete](file:///e:/workspace/win-share-panel/src/pages/Shares.tsx) 模式。
- **样式一致**：新组件 `.glass-card` 类 + Tailwind 色彩变量（`primary/secondary/accent/ink/fog/mist`）+ antd v6 `items` 数组形式；Drawer/Modal 面板用 `styles.body` 设磨砂玻璃背景。

## Critical Files for Implementation

- `e:\workspace\win-share-panel\src\stores\uiStore.ts`（新建，全方案状态中枢，所有后续阶段依赖）
- `e:\workspace\win-share-panel\src\hooks\useHotkeys.ts`（新建，热键意图写入 store）
- `e:\workspace\win-share-panel\src\components\HealthBar.tsx`（新建，顶栏告警）
- `e:\workspace\win-share-panel\src\components\PermissionDrawer.tsx`（新建，权限编辑抽屉）
- `e:\workspace\win-share-panel\src\components\PermissionMatrix.tsx`（新建，矩阵视图 + 导出）
- `e:\workspace\win-share-panel\src\pages\Shares.tsx`（重度改造：权限按钮 + 编辑 + 批量 + 搜索 + hotkey tick 落地）
- `e:\workspace\win-share-panel\src\pages\Sessions.tsx`（实时刷新 + 批量断开 + 气泡）
- `e:\workspace\win-share-panel\src\pages\Dashboard.tsx`（PNG/CSV 导出）
- `e:\workspace\win-share-panel\src\pages\Settings.tsx`（快照历史 Tab）
- `e:\workspace\win-share-panel\src\pages\Users.tsx`（权限矩阵 Tab）
- `e:\workspace\win-share-panel\src\components\CommandPalette.tsx`（跨域搜索 + 动态命令）
- `e:\workspace\win-share-panel\src\App.tsx` + `Layout.tsx`（接入 store + HealthBar + RouteSync）
- `e:\workspace\win-share-panel\electron\services\user.ts`（setSharePermissions 补 Block/Unblock）
- `e:\workspace\win-share-panel\electron\services\smb.ts`（快照/回滚后端）
- `e:\workspace\win-share-panel\electron\main.ts`（window:balloon IPC）
- `e:\workspace\win-share-panel\electron\preload.ts` + `ipc\index.ts` + `types.ts` + `src/types.ts`（四处类型同步）

## 工作量评估

| 阶段 | 新建 | 修改 | 复杂度 | 工时 |
|---|---|---|---|---|
| 1 基础 | 4 | 3 | 低 | 0.5 天 |
| 2 权限 UI | 2 | 4 | 高 | 1.5 天 |
| 3 共享增强 | 0 | 1 | 中 | 1 天 |
| 4 韧性 | 0 | 8 | 高 | 1.5 天 |
| 5 搜索增强 | 0 | 1 | 中 | 0.5 天 |
| **合计** | **6** | **~17** | — | **约 5 天** |

## 推荐开发顺序（严格串行）

1. 阶段 1 基础先行（store 是后续依赖）
2. 阶段 2 后端增强（`user.ts` Block/Unblock，独立小改先合入便于联调）
3. 阶段 3 共享增强（验证 store tick 机制）
4. 阶段 2 权限 UI（store 与 hotkey 稳定后上复杂组件）
5. 阶段 4 韧性（后端快照 + 前端会话刷新 + 仪表板导出，可拆分并行）
6. 阶段 5 搜索增强（收尾，依赖全部 API）

## 端到端验证

完成全部阶段后执行端到端验证：

1. `pnpm dev` 启动，确认无报错
2. Ctrl+K 全局搜索"admin" → 跨域结果分组显示 → 回车跳转
3. 共享管理：搜索过滤、勾选批量启停/删除、Ctrl+N 新建、Del 批量删、Space 批量切换、F5 刷新、点"权限"打开 Drawer 编辑 Deny 保存验证、点"编辑"改 description、拖文件夹快速创建
4. 用户权限：本地用户/组列表、权限矩阵加载完成、导出 CSV/JSON
5. 会话监控：5s 实时刷新、勾选批量断开、Del 触发批量断开、用别机 `net use` 触发托盘气泡
6. SMB 配置：改配置保存 → 快照历史看到新条目 → 回滚后配置恢复且无新快照
7. 仪表板：导出 PNG/CSV 文件可打开
8. 健康检查：`Stop-Service LanmanServer` ≤30s 顶栏告警，恢复后消失
9. `pnpm typecheck` 通过（验证四处类型同步无误）
10. `pnpm build` 构建产物无报错
