; WinShare Panel —— NSIS 安装器钩子（v1.4.0 起）
;
; 本文件由 electron-builder 通过 `nsis.include` 注入（`nsisScriptGenerator` 把
; 自定义 include 追加到脚本头部，先于所有 Section 编译），因此本文件里的
; `!macro` 定义会在 installSection.nsh / uninstaller.nsh 引用 customInstall /
; customUnInstall 之前完成编译——这是 electron-builder 预留的两个扩展点：
;   - installSection.nsh：写 ARP / 建快捷方式之后 `!ifmacrodef customInstall`
;   - uninstaller.nsh：删 ARP / 删快捷方式之后 `!ifmacrodef customUnInstall`
; 用 `!macro`（而非覆盖 `.onInit` / `un.onInit` 函数）接入，避免与 electron-builder
; 自带逻辑冲突（自带逻辑已写好 ARP 条目，覆盖函数会导致 ARP 写两次 / 卸载器
; 行为错乱）。
;
; === 安装时补写（electron-builder 模板已负责 ①②，本文件只补 ③④）===
; ① ARP 程序列表（控制面板「程序和功能」）
;    electron-builder 模板自带：HKLM\...\Uninstall\WinSharePanel（per-user 装则
;    写 HKCU），含 DisplayName/DisplayIcon/DisplayVersion/UninstallString/
;    Publisher/InstallLocation/NoModify/NoRepair，装完即可在「程序和功能」里
;    识别、卸载、修改安装目录
; ② 开始菜单 / 桌面快捷方式
;    electron-builder.yml 里 `createStartMenuShortcut: true` /
;    `createDesktopShortcut: true` 模板已处理（addStartMenuLink /
;    createDesktopShortcut 两个内置 Section）
; ③ 登录快速启动（本文件补写）
;    HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WinSharePanel
;    = "$INSTDIR\WinShare Panel.exe" --silent
;    登录时静默拉起；主进程单实例锁（main.ts）让第二个实例激活第一个。
;    卸载时由 customUnInstall 删除。
; ④ Windows 服务预留标记（本文件补写）
;    HKLM\Software\WinSharePanel\ServicePending = 1
;    v1.4.0 暂不创建服务本体——GUI 应用形态与 Windows 服务（会话 0，无窗口
;    交互）冲突；真正做服务需 NSSM / 计划任务包装器，留待后续版本。主进程
;    system:servicePending IPC 通道读这个标记决定是否在应用内提示「可一键
;    注册为系统服务」。卸载时删除。
; ⑤ 系统 PATH 条目：**安装器不写**（跨权限 + 卸载残留风险），改由主进程
;    electron/services/pathRegistration.ts 的「一键注册/撤销」入口写用户级
;    HKCU\Environment\Path。本文件只在卸载时弹消息框提示用户手动确认清理
;    （NSIS 卸载器无法精确判断 PATH 里哪一段是本应用加的，避免误删用户自己
;    配置的路径）
;
; 依赖的宏（全部由 electron-builder 自带模板/内置 NSIS 提供，本文件不重定义）：
;   ${If} ${IfNot} ${EndIf} ${Silent} —— NSIS v3 内置（makensis 自带逻辑系统）
;   ${StrContains} —— app-builder-lib/templates/nsis/include/StrContains.nsh
;   （脚本生成器 `scriptGenerator.addIncludeDir(nsisTemplatesDir/include)` 已
;     加入 include 搜索路径，`!include "StrContains.nsh"` 即可解析）

!include "StrContains.nsh"

!define WS_PRODUCT_KEY "WinSharePanel"
!define WS_RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

; === ③ + ④：安装器写 登录快速启动 + 服务预留标记 ===
; 在 electron-builder 写完 ARP / 建好快捷方式之后执行（$INSTDIR 此时已定）
!macro customInstall
  ; ③ 登录快速启动（HKCU Run 键，跟随当前用户；--silent 由主进程识别并静默最小化）
  WriteRegStr HKCU "${WS_RUN_KEY}" "${WS_PRODUCT_KEY}" \
    '"$INSTDIR\WinShare Panel.exe" --silent'

  ; ④ Windows 服务预留标记（主进程首启自检读取；v1.4.0 不创建服务本体）
  WriteRegStr HKLM "Software\${WS_PRODUCT_KEY}" "ServicePending" "1"
!macroend

; === 卸载器：删 ③ + ④，提示 ⑤ ===
; 在 electron-builder 删完 ARP / 快捷方式之后执行
!macro customUnInstall
  ; ③ 登录快速启动（HKCU Run 键）
  DeleteRegKey HKCU "${WS_RUN_KEY}\${WS_PRODUCT_KEY}"
  ; ④ 服务预留标记
  DeleteRegKey HKLM "Software\${WS_PRODUCT_KEY}"
  ; ⑤ PATH 清理提示：仅非静默卸载时弹框（静默 /S 下弹框会阻塞且无人应答）
  ${IfNot} ${Silent}
    Call un.winshareShowPathHint
  ${EndIf}
!macroend

; 卸载器侧的 PATH 清理函数（un. 前缀 = 卸载器段）
; 弹框让用户确认；「是」→ 提示去「系统 → 高级系统设置 → 环境变量」手动清理，
; 并在消息里给出当前用户 PATH 的可复制提示（NSIS 不做自动改 PATH——误删风险）
; NSIS MessageBox 的换行是字面量 \n（双引号字符串内的转义），
; ${NL} 不是 NSIS 内置常量（v3 无此宏），用了会被 makensis 报 6000 警告 →
; electron-builder 把警告当错误处理，构建直接失败（v1.4.1 CI 实测踩坑）
Function un.winshareShowPathHint
  MessageBox MB_OK \
    "WinShare Panel 可能已通过应用内「一键注册」向当前用户 PATH 追加了安装目录。\n卸载不会自动清理 PATH 条目（避免误删你配置的其他路径）。\n\n如需清理，请打开「设置 → 系统 → 关于 → 高级系统设置 → 环境变量 → 用户环境变量 Path」，删除其中的 ${WS_PRODUCT_KEY} 安装目录项。" \
    IDOK
FunctionEnd
