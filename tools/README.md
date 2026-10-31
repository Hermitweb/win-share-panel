# tools/ — WPF 面板 UIA 自动化脚本

驱动 `net/WinShare.App` 的 Debug 构建做冒烟/截图/性能探测（Windows UI Automation，
不经 dotnet test，与 xUnit 门禁互补）。所有路径从脚本位置推导，仓库可克隆到任意目录使用。

## 脚本

| 脚本 | 用途 |
|---|---|
| `start-app.ps1` | 杀旧进程 → 启动 Debug exe → 等 22s → 输出 PID/Responding/HWND（`-Configuration` 可选 Release） |
| `ui-smoke.ps1` | 6 页逐页标记断言 + 30 次随机切页压测（耗时/失败数/内存） |
| `ui-tabs.ps1` | 枚举各页页签结构 |
| `ui-strict.ps1` / `ui-reach.ps1` | 页面控件可达性 / 空 BoundingRectangle 计数（布局塌陷诊断） |
| `cpu-check.ps1` | 空闲 CPU（15s）+ 切页 10 次后 CPU/内存 |
| `shot.ps1 -NavIndex 0..5` | PrintWindow 截图当前导航页 → `tools/shot.png` |

## 运行注意（PowerShell 5.1）

脚本为 UTF-8 无 BOM 编码（按 pwsh 书写）。Windows PowerShell 5.1 会把无 BOM 的
UTF-8 按 GBK 解读，中文字符串变乱码并可能解析失败。本机未装 pwsh 时，先做 BOM
副本再运行：

```powershell
# 例：用 Python 生成 utf-8-sig 副本后执行
python -c "import io;io.open(r'.repro/smoke/ui-smoke.ps1','w',encoding='utf-8-sig').write(io.open(r'tools/ui-smoke.ps1',encoding='utf-8').read())"
powershell -NoProfile -ExecutionPolicy Bypass -File .repro/smoke/ui-smoke.ps1
```

## 已知环境事实

- 本机火绒（HipsDaemon）会拦截未签名 Debug 构建对 `%APPDATA%\WinSharePanel` 的写入与
  PowerShell 子进程调用——表现为日志/状态零写入、仪表板数据为空（处置见 `App.xaml.cs` 注释）。
- 数据为空不冒烟失败：`ui-smoke.ps1` 断言的是页面结构标记（标题/按钮/表格存在性），不依赖 PS 数据。
