; WinShare Panel WPF 安装包脚本（Inno Setup 6）
; ISCC /D 参数注入（见 .github/workflows/release.yml 的 wpf-release job）：
;   /DAppVersion=2.0.1  /DArch=x64|x86|arm64  /DArchLabel=x64|x86|arm64
;   /DPublishDir=<self-contained 发布目录（绝对路径）>  /DOutputDir=<release 目录（绝对路径）>
; 产物：WinShare.Panel-{AppVersion}-Setup-{ArchLabel}.exe

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
DefaultDirName={autopf}\{#AppName}
ArchitecturesInstallIn64BitMode={#Install64BitMode}
OutputBaseFilename=WinShare.Panel-{#AppVersion}-Setup-{#ArchLabel}
OutputDir={#OutputDir}
DefaultGroupName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; 共享管理需要管理员令牌（与“以管理员身份重启”语义一致）
PrivilegesRequired=admin
; 图标：绝对路径由流水线 /DIconFile 注入（Inno 会把相对路径解析到进程 CWD 而非
; iss 所在目录——CI 上该相对路径曾报“找不到路径”，勿改回相对写法）
#ifndef IconFile
#define IconFile "icon.ico"
#endif
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
