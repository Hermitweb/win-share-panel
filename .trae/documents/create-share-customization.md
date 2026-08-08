# 新建共享高度可自定义设置

## Context

当前「新建共享」表单的自定义能力不足，尤其 SMB 协议：
- **最大差距**：创建时无法设置账户级访问控制（Full/Change/Read/NoAccess），用户只能创建后手动到权限面板设置或依赖预设模板
- SMB 的 `encryptData`（共享级数据加密）、`shareShadowCopy`（VSS 卷影副本）后端支持但 UI 未暴露
- `concurrentUserLimit`/`cachingMode`/`folderEnumerationMode` 虽在 UI 中有，但适配器未传递，靠创建后单独 `share:update` 设置（多一次 IPC+PowerShell 往返，且失败时静默忽略）
- NFS 的 `anonymousUid`/`anonymousGid` 后端支持但 UI 未暴露

目标：让用户在创建共享时一步到位完成所有配置，无需创建后再补设。

## 实现步骤

### 1. 类型扩展 — `electron/types.ts`

在 `CreateShareInput` 接口补充 SMB 高级字段（`noAccess`、`concurrentUserLimit`、`cachingMode`、`folderEnumerationMode`、`encryptData`、`shareShadowCopy`）。这些字段在 `CreateShareOpts`（L265-283）中已定义，只需在 `CreateShareInput`（L324-347）中对齐。

### 2. SMB 适配器修复 — `electron/services/protocol/adapters/smbAdapter.ts`

`createShare`（L36-46）当前仅传递 7 个字段，改为传递全部 `CreateShareOpts` 支持的字段（`noAccess`、`encryptData`、`concurrentUserLimit`、`cachingMode`、`folderEnumerationMode`、`shareShadowCopy`），消除前端单独 `share:update` 的需要。

### 3. IPC 输入校验 — `electron/ipc/index.ts`

在 `adapter:create` handler 中，对 `fullAccess`/`changeAccess`/`readAccess`/`noAccess` 数组字段增加校验：确保是 `string[]` 且每个元素为非空字符串。复用现有的 `requireProtocol`/`requireName`/`requirePath` 模式。

### 4. 前端数据获取 — `src/pages/Shares.tsx`

在 `load()` 中并行获取本地用户和组列表（`api.user.list()` + `api.user.groups()`），用于 SMB 访问控制的 Select 选项。用户名和组名合并为选项列表。

### 5. 前端创建表单 UI — `src/pages/Shares.tsx`

**SMB 部分（新增访问控制 + 高级选项）：**
- 新增「访问控制」区域，三个 `Select mode="multiple"` 分别为：完全控制（FullAccess）、更改（ChangeAccess）、读取（ReadAccess）
- 选项来自步骤 4 获取的用户/组列表，带图标区分用户/组
- 新增 `encryptData`（共享级加密）Switch — 注意与 `encrypted`（SMB 加密）的区别说明
- 新增 `shareShadowCopy`（卷影副本）Switch
- `concurrentUserLimit`/`cachingMode`/`folderEnumerationMode` 保留在表单中

**NFS 部分（新增 UID/GID）：**
- 新增 `anonymousUid`（匿名 UID）和 `anonymousGid`（匿名 GID）InputNumber 字段，放在「允许 root 访问」之后

**布局原则**（遵循用户偏好）：
- 访问控制和高级选项用 `Collapse` 折叠面板包裹，默认收起，保持表单紧凑
- 紧凑间距（margin 6→4, gap 8→6）

### 6. 前端 handleCreate 简化 — `src/pages/Shares.tsx`

将所有 SMB 字段（含新增的 `fullAccess`/`changeAccess`/`readAccess`/`encryptData`/`shareShadowCopy`/`concurrentUserLimit`/`cachingMode`/`folderEnumerationMode`）直接传入 `api.adapter.create()`，删除创建后单独调用 `api.share.update()` 设置高级属性的代码块（L244-257）。

## 涉及文件

| 文件 | 变更 |
|------|------|
| `electron/types.ts` | `CreateShareInput` 补充 6 个 SMB 字段 |
| `electron/services/protocol/adapters/smbAdapter.ts` | `createShare` 传递全部字段 |
| `electron/ipc/index.ts` | `adapter:create` 增加数组字段校验 |
| `src/pages/Shares.tsx` | 表单 UI + 数据获取 + handleCreate 简化 |

## 验证

1. `pnpm typecheck` — 类型检查通过
2. `pnpm build` — 构建通过
3. 手动验证：打开新建共享弹窗 → 选 SMB → 展开访问控制 → 选择用户/组 → 填写高级选项 → 创建 → 确认共享属性一步到位（无需后续 update）
4. 手动验证：选 NFS → 填写 anonymousUid/Gid → 创建成功
