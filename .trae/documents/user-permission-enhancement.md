# 用户与权限板块自定义化增强

## Context

用户反馈「用户与权限」板块自定义化自由度不够。当前功能盘点：
- **用户**：列表/搜索/创建/编辑(全名/描述/启用/密码策略/重设密码)/删除/启停。**缺失**：重命名（后端有 `renameUser` 但 UI 未暴露）、批量操作、用户详情聚合视图（所属组+共享权限）、密码生成器
- **组**：列表/创建/删除/管理成员(逐个添加)/编辑描述。**缺失**：组重命名、批量添加成员、创建时分配用户
- **权限矩阵**：只读视图 + CSV/JSON 导出。**缺失**：无法直接编辑

目标：将用户与权限板块提升到与 SMB 配置同等深度的可定制能力。

## 实现步骤

### Step 1: 后端 — 组重命名 (`electron/services/user.ts`)

新增 `renameGroup(name, newName)` 函数，使用 `Rename-LocalGroup` cmdlet（与 `renameUser` 同模式）。内置组保护检查复用 `deleteGroup` 的 `protectedGroups` 列表。

### Step 2: IPC + Preload — 暴露组重命名

- `electron/ipc/index.ts`：追加 `ipcMain.handle('group:rename', ...)`
- `electron/preload.ts`：`group` 对象追加 `rename: (oldName, newName) => ...`
- `src/types.ts`：`WinShareApi.group` 追加 `rename` 签名

### Step 3: 密码生成器工具 (`src/utils/password.ts` 新建)

客户端纯函数，无后端依赖：
- `generatePassword(length, options)` — 支持 大小写字母/数字/特殊字符 开关
- `evaluateStrength(pwd)` — 返回 'weak' | 'medium' | 'strong'
- 默认 12 位、含大小写+数字+特殊字符

### Step 4: UserDetailDrawer 组件 (`src/components/UserDetailDrawer.tsx` 新建)

替换现有 `UserEditModal`，采用 `ShareDetailDrawer` 的 Drawer+Tabs 模式：

**Tab 1 "属性"**：
- 可编辑表单：用户名（重命名，调用 `api.user.rename`）、全名、描述、启用、允许改密、密码永不过期
- 密码区：展开式重设密码 + 「生成密码」按钮 + 强度指示条
- 只读信息：SID、来源、上次设密、上次登录

**Tab 2 "所属组"**：
- 展示当前用户所属组（Tag 列表）
- 添加到组：Select(available groups) + 添加按钮，调用 `api.group.addMember`
- 从组移除：每个 Tag 带 close，调用 `api.group.removeMember`

**Tab 3 "共享权限"**：
- 调用 `api.user.sharePermissions(name)` 获取该用户在各共享上的权限
- Table 展示：共享名 | 访问级别(Tag) | 拒绝(Tag)
- 支持 SMB/NFS/FTP/WebDAV 权限（`sharePermissions` 返回所有协议的 SMB 权限；多协议权限需调 `adapter.permissions`）

### Step 5: 批量操作 (`src/pages/Users.tsx` 修改)

用户表增加 `rowSelection`（checkbox 多选）：
- 选中时顶部显示批量操作栏：批量启用 / 批量禁用 / 批量删除 / 分配到组
- 「分配到组」弹出 Modal，Select 选择目标组，批量调 `api.group.addMember`
- 选中计数显示在状态栏

### Step 6: GroupManageModal 增强 (`src/components/GroupManageModal.tsx` 修改)

- 组名可编辑：添加「重命名」按钮，弹 Input 确认，调 `api.group.rename`
- 成员添加改为 Select(multiple) 从用户列表选择，支持批量添加
- 保留手动输入回车添加（兼容域账号等非本地用户）

### Step 7: UserCreateModal 增强 (`src/components/UserCreateModal.tsx` 修改)

- 密码字段追加「生成」按钮 + 强度指示
- 新增「分配到组」多选 Select（创建后自动加入所选组）
- 密码确认字段同步生成密码

### Step 8: Settings/路由更新

- `src/pages/Users.tsx`：import 替换 `UserEditModal` → `UserDetailDrawer`，`editUser` state 类型不变
- 删除 `UserEditModal.tsx`（被 Drawer 完全替代）

### Step 9: 验证

1. `pnpm typecheck` 通过
2. 重启 dev server（主进程改动：group:rename IPC handler）
3. 手动测试：用户重命名、批量操作、组重命名、密码生成、用户详情三 Tab 切换、创建时分配组

## 关键文件

| 文件 | 改动 |
|------|------|
| `electron/services/user.ts` | 新增 `renameGroup` |
| `electron/ipc/index.ts` | 新增 `group:rename` handler |
| `electron/preload.ts` | group 追加 `rename` |
| `src/types.ts` | WinShareApi.group 追加 `rename` |
| `src/utils/password.ts` | 新建：密码生成 + 强度评估 |
| `src/components/UserDetailDrawer.tsx` | 新建：用户详情抽屉(3 Tab) |
| `src/components/GroupManageModal.tsx` | 修改：组重命名 + 批量添加成员 |
| `src/components/UserCreateModal.tsx` | 修改：密码生成 + 创建时分配组 |
| `src/pages/Users.tsx` | 修改：批量操作 + 替换 Modal→Drawer |
| `src/components/UserEditModal.tsx` | 删除（被 Drawer 替代） |

## 复用现有能力

- `api.user.rename(oldName, newName)` — 后端+IPC+preload 已就绪，UI 未暴露
- `api.user.sharePermissions(name)` — 获取用户共享权限，用于详情 Tab 3
- `api.user.ntfsPermissions(path)` — 获取 NTFS ACL（PermissionDrawer 已用）
- `api.group.addMember/removeMember` — 用于用户详情 Tab 2 组成员管理
- `ShareDetailDrawer` 的 Drawer+Tabs 模式 — 作为 UserDetailDrawer 的结构参考
- `PermissionMatrix` 的 `pickAccess` 逻辑 — 复用权限聚合显示
