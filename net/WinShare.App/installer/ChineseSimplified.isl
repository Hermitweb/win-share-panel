; WinShare Panel 安装向导简体中文语言包（部分覆盖：缺失消息自动回退英文 Default.isl）
; 覆盖安装向导全流程用户可见文案；格式为 Inno Setup 6 [LangOptions] + [Messages]。

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
TitleFontName=Microsoft YaHei UI
TitleFontSize=11
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=12

[Messages]
; ===== 向导标题/页面 =====
SetupAppTitle=安装 - %1
SetupWindowTitle=安装 - %1
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=本向导将指导你在电脑上安装 [name/ver]。%n%n建议在继续之前关闭其他所有应用程序。
SelectDirDesc=选择安装位置
SelectDirLabel3=安装向导将把 [name] 安装到以下文件夹。%n%n点击“下一步”继续。%n%n如需安装到其他文件夹，点击“浏览”。
SelectDirBrowseLabel=点击“下一步”继续。如需更改文件夹，点击“浏览”。
SelectDirExists=该文件夹已存在。是否仍安装到该文件夹？
SelectProgramGroupDesc=选择开始菜单文件夹
SelectTasksDesc=选择要执行的附加任务
SelectTasksLabel2=选择安装 [name] 时要执行的附加任务，然后点击“下一步”。
ReadyDesc=准备安装
ReadyLabel1=准备开始安装 [name]
ReadyLabel2a=点击“安装”开始安装，或点击“上一步”复查或修改设置。
ReadyLabel2b=点击“安装”开始安装。
ReadyMemoDir=目标位置：
ReadyMemoTasks=附加任务：
InstallingLabel=正在安装 [name]，请稍候…
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已成功安装到你的电脑。%n%n点击“完成”关闭安装向导。
FinishedLabel=[name] 已成功安装到你的电脑。%n%n点击“完成”关闭安装向导。
; ===== 按钮 =====
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
; ===== 目录页 =====
BrowseDialogLabel=在下面的文件夹列表中选择安装位置
MustExistErr=文件夹不存在
DoesntExistErr=文件夹不存在。是否创建该文件夹？
CreatedNewFolder=已创建新文件夹：
; ===== 任务 =====
AdditionalIcons=附加任务：
CreateDesktopIcon=创建桌面快捷方式(&D)
LaunchProgram=运行 [name]
; ===== 安装过程 =====
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。现在退出吗？%n%n下次运行安装程序时可以继续安装。
ClickFinish=点击“完成”结束安装。
StatusExtracting=正在解压文件…
StatusClosingApplications=正在关闭应用程序…
StatusRollback=正在回滚更改…
StatusDeletingFiles=正在删除文件…
; ===== 卸载 =====
UninstallAppTitle=卸载 %1
UninstallAppFullTitle=%1 卸载
UninstallStatusLabel=正在卸载 [name]，请稍候…
UninstallAppDoneLabel=[name] 已从电脑中移除。
ConfirmUninstall=确定要完全移除 %1 及其所有组件吗？
UninstallNotFound=应用程序未能从注册表中定位到卸载信息。
