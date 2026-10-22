# P1 数据源为空 · 结案报告（2026-10-22）

## 结论（三句话）

1. **WinShare.Core 数据层完全正常**：独立探针（.repro\ProbeDisk，同款 PsRunner + DiskService）拿到 4 个磁盘全部数据；pwsh 手工复刻应用通道（5.1 + EncodedCommand + ConvertTo-Json）同样全量返回。
2. **应用宿主内全部本地 I/O 与 PS 子进程被安全软件按进程拦截**：探针实锤 UnauthorizedAccessException——对 logs 目录连**新建文件**都拒绝（new-file-write FAIL），同用户同提权的 pwsh 却完全可写。本机运行 **火绒安全（HipsDaemon/HipsTray，D:\Huorong\Sysdiag）**。
3. 之前「渲染 bug」与「数据源为空」是两个独立问题：前者 = DockPanel LastChildFill 布局缺陷（已修，见 ROOTCAUSE.md 与 verify 截图）；后者 = 火绒按进程拦截未签名 Debug 构建的 I/O（本次结案，处置在用户侧）。

## 症状 → 证据链

| 症状 | 证据 |
|---|---|
| 日志零写入（app.log 停在 10/21 21:58） | 探针：LogSink.Info 调用后 size 不变；直接 Append 抛 UnauthorizedAccess |
| appstate.json 停在 21:55（状态不持久化） | 同一拦截面（StateStore 静默吞异常） |
| 仪表板统计 0 / 磁盘探测失败 | PS 子进程或其输出被拦（Core 层同代码探针全正常） |
| 有时能拿到数据（曾见共享总数=1） | 火绒启发式按进程信誉放行不稳定 |
| pwsh 同身份可写、应用不行 | 按进程（路径/签名）拦截的典型指纹 |

## 处置（用户侧，三选一）

1. **火绒信任区**：把 WinShare.App.exe（或整个 E:\workspace\win-share-panel\net\...\bin 目录）加入火绒「信任区/白名单」（推荐，最快）。
2. **代码签名**：发布签名构建后按签名放行（治本，随发版任务做）。
3. **暂用现有 Release 发布产物**：bin\Release\net10.0-windows\win-x64 下的旧产物若已被火绒放行可临时对照验证。

应用侧**不做**降级掩盖（备用目录/重试风暴），避免把安全拦截静默化——见 App.xaml.cs 内注释。

## 期间改动

- App.xaml.cs：探针已全部移除，留一段中文注释说明该环境事实。
- .repro\ProbeDisk\：独立宿主探针（复现工程，随 .repro 一并保留）。
- FEATURE-AUDIT.md：旧类名 ProtocolTabView → ProtocolSectionView 同步完成（5 处）；release-notes-wpf-1.0.1.md 为历史发布说明，按惯例不改历史文档。

## 最终验证

- dotnet build：0 错误
- WinShare.App.Tests：323/323 全绿
- WinShare.Core.Tests：327/327 全绿（本轮早前已验）
- 验证实例已清零（PID 46808 等已全部关闭）
