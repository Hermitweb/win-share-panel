; WinShare Panel WPF 安装包脚本（Inno Setup 6.3+）
; ISCC /D 参数注入（见 .github/workflows/release.yml 的 wpf-release job）：
;   /DAppVersion=2.0.1  /DArch=x64|x86|arm64  /DArchLabel=x64|x86|arm64
;   /DPublishDir=<self-contained 发布目录（绝对路径）>  /DOutputDir=<release 目录（绝对路径）>
; 产物：WinShare.Panel-{AppVersion}-Setup-{ArchLabel}.exe
;
; v2.2.0 安装与运行配置（本版新增）：
;   - 安装位置：管理员（Program Files）与普通用户（%LOCALAPPDATA%\Programs）双模式，
;     向导可选目录（PrivilegesRequiredOverridesAllowed——非管理员环境可跳过 UAC 完成安装）
;   - 任务项：桌面快捷方式（默认勾选）/ 开机自动启动（HKLM Run，全部用户）/ 加入系统 PATH
;   - 卸载清理：删除自启 Run 值与 PATH 中的安装目录；用户数据（%APPDATA%\WinSharePanel）
;     默认保留（升级/重装不丢配置）
;   - 运行环境：发布为 self-contained（.NET 10 运行时已捆绑，无需预装框架）；
;     MinVersion 强制 Windows 10+（应用 TFM 为 windows10.0.17763 = Win10 1809+）
;   - 静默安装：WinShare.Panel-x.y.z-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES
;     /DIR="D:\自定义位置" /TASKS="desktopicon,autostart,addpath"（TASKS 用 * 表示全部）
;   - 服务注册语义：本应用为 GUI 控制面板，不注册 Windows 服务；SMB/NFS/FTP/WebDAV
;     等共享服务由 Windows 自身承载（面板负责配置）。「服务注册」落地为：开机自启 Run 键
;     （与应用内「开机自启」开关同键名 WinSharePanel，键域 HKLM=全部用户；应用内开关
;     写 HKCU=当前用户——安装器勾选 + 应用内开启不会双开，单实例守卫会去重）

#define AppName "WinShare Panel"
#define AppExe "WinShare.App.exe"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#ifndef Arch
#define Arch "x64"
#endif
#ifndef ArchLabel
#define ArchLabel "x64"
#endif
#ifndef PublishDir
#define PublishDir "publish"
#endif
#ifndef OutputDir
#define OutputDir "release"
#endif

; 64 位架构装进 Program Files / Program Files (arm64)；x86 落 Program Files (x86)
#if Arch == "x64"
#define Install64BitMode "x64"
#elif Arch == "arm64"
#define Install64BitMode "arm64"
#else
#define Install64BitMode ""
#endif

[Setup]
; 稳定 AppId：同架构升级共用卸载项（跨架构互覆盖由 Windows Installer 语义自然处理）
AppId={{8F4B0A6E-1D3C-4E9A-B7F2-5C8D2A1E6B93}
AppVersion={#AppVersion}
AppName={#AppName}
AppPublisher=WinShare
; v2.2.0：{autopf} 随安装模式自动解析——管理员：Program Files；普通用户：%LOCALAPPDATA%\Programs
DefaultDirName={autopf}\{#AppName}
ArchitecturesInstallIn64BitMode={#Install64BitMode}
OutputBaseFilename=WinShare.Panel-{#AppVersion}-Setup-{#ArchLabel}
OutputDir={#OutputDir}
DefaultGroupName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; v2.2.0：强制 Windows 10+（应用 TFM = windows10.0.17763，Win10 1809+）
MinVersion=10.0
; 共享管理需要管理员令牌（与“以管理员身份重启”语义一致）；
; v2.2.0：允许普通用户环境跳过 UAC 走 per-user 安装（对话框或命令行 /CURRENTUSER）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
; 图标：绝对路径由流水线 /DIconFile 注入（Inno 会把相对路径解析到进程 CWD 而非
; iss 所在目录——CI 上该相对路径曾报“找不到路径”，勿改回相对写法）
#ifndef IconFile
#define IconFile "icon.ico"
#endif
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
; Default.isl 前置兜底（英文），ChineseSimplified.isl 覆盖中文键——
; 部分覆盖语言包若未兜底 Default.isl，未覆盖键（CreateDesktopIcon 等）会报
; "has not been defined"（v2.0.2 首跑三架构全挂的真实原因）
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
; v2.2.0：桌面快捷方式默认勾选（此前 unchecked——安装阶段按需求明确创建）
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务：";
; v2.2.0：开机自动启动（写 HKLM Run——全部用户登录时启动；与应用内「开机自启」开关
; （HKCU 当前用户）同键名 WinSharePanel，单实例守卫会去重；卸载时自动删除）
Name: "autostart"; Description: "开机自动启动 WinShare Panel（当前系统全部用户）"; GroupDescription: "服务与运行："; Flags: unchecked
; v2.2.0：安装目录加入系统 PATH（命令行可直接启动 WinShare.App.exe）
Name: "addpath"; Description: "将安装目录加入系统 PATH 环境变量"; GroupDescription: "服务与运行："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; v2.3.1：开机自启的 Run 值**不再**用静态 [Registry] 段。
; 静态段无条件写 HKLM —— 而本脚本允许普通用户环境跳过 UAC 走 per-user 安装
; （PrivilegesRequiredOverridesAllowed=dialog commandline），此时写 HKLM 必然
; RegSetValueEx failed; code 5（拒绝访问），安装向导直接弹错误框。
; 改为与 PATH 同一套「按安装模式选键域」的逻辑：管理员→HKLM（全部用户），
; per-user→HKCU（当前用户）；卸载时两个域都清一遍。

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// v2.2.0：PATH 环境变量注册（安装时追加、卸载时移除；不覆盖既有 PATH）
// 管理员模式 → 机器级 PATH（HKLM\...\Session Manager\Environment）；
// 普通用户模式 → 用户级 PATH（HKCU\Environment）——与「按需权限」语义一致。

const
  PathKeyMachine = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  PathKeyUser = 'Environment';
  RunKeyMachine = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';

function PathRootAndKey(): String;
begin
  if IsAdminInstallMode() then
    Result := 'HKLM\' + PathKeyMachine
  else
    Result := 'HKCU\' + PathKeyUser;
end;

procedure AddToPath();
var
  RootKey: Integer;
  Key, OldPath, AppDir: String;
begin
  if IsAdminInstallMode() then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    Key := PathKeyMachine;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    Key := PathKeyUser;
  end;
  AppDir := ExpandConstant('{app}');
  if RegQueryStringValue(RootKey, Key, 'Path', OldPath) then
  begin
    if Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OldPath) + ';') > 0 then
      Exit; // 已存在，不重复追加
    RegWriteStringValue(RootKey, Key, 'Path', OldPath + ';' + AppDir);
  end
  else
    RegWriteStringValue(RootKey, Key, 'Path', AppDir);
end;

procedure RemoveFromPath();
var
  RootKey: Integer;
  Key, OldPath, AppDir, P, Head, Tail: String;
begin
  if IsAdminInstallMode() then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    Key := PathKeyMachine;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    Key := PathKeyUser;
  end;
  AppDir := ExpandConstant('{app}');
  if not RegQueryStringValue(RootKey, Key, 'Path', OldPath) then
    Exit;
  P := ';' + Uppercase(OldPath) + ';';
  if Pos(';' + Uppercase(AppDir) + ';', P) = 0 then
    Exit;
  Head := Copy(P, 1, Pos(';' + Uppercase(AppDir) + ';', P));
  Tail := Copy(P, Pos(';' + Uppercase(AppDir) + ';', P) + Length(AppDir) + 2, MaxInt);
  // 去掉该目录后重组（去掉首尾哨兵分号）
  OldPath := Head + Tail;
  if Copy(OldPath, 1, 1) = ';' then Delete(OldPath, 1, 1);
  if Copy(OldPath, Length(OldPath), 1) = ';' then Delete(OldPath, Length(OldPath), 1);
  RegWriteStringValue(RootKey, Key, 'Path', OldPath);
end;

procedure WriteAutostart();
var
  RootKey: Integer;
  Cmd: String;
begin
  if not WizardIsTaskSelected('autostart') then
    Exit;
  // 键域跟随安装模式：管理员装 → HKLM（全部用户登录时启动）；per-user 装 → HKCU（当前用户）。
  // 写 HKLM 在 per-user 模式下必然 code 5（拒绝访问）——那正是 v2.3.0 安装报错的根因。
  if IsAdminInstallMode() then
    RootKey := HKEY_LOCAL_MACHINE
  else
    RootKey := HKEY_CURRENT_USER;
  Cmd := '"' + ExpandConstant('{app}') + '\' + '{#AppExe}' + '" --silent';
  RegWriteStringValue(RootKey, RunKeyMachine, 'WinSharePanel', Cmd);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addpath') then
      AddToPath();
    WriteAutostart();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveFromPath();
    // 开机自启兜底清理（两个域都清：管理员装写 HKLM、per-user 装写 HKCU；
    // HKCU 那一路也由应用内开关自行管理，用户数据保留）
    RegDeleteValue(HKEY_LOCAL_MACHINE, RunKeyMachine, 'WinSharePanel');
    RegDeleteValue(HKEY_CURRENT_USER, RunKeyMachine, 'WinSharePanel');
  end;
end;
