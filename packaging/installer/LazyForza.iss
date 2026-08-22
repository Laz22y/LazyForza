#ifndef AppVersion
  #error AppVersion define is required
#endif
#ifndef PublishDir
  #error PublishDir define is required
#endif
#ifndef NumericVersion
  #error NumericVersion define is required
#endif
#ifndef OutputDir
  #error OutputDir define is required
#endif

#define AppName "LazyForza"
#define AppExeName "LazyForza.App.exe"

[Setup]
AppId={{8AD5F00C-0F59-4A56-9412-B98C6428153A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=LazyForza
AppPublisherURL=https://github.com/Laz22y/LazyForza
AppSupportURL=https://github.com/Laz22y/LazyForza/issues
AppUpdatesURL=https://github.com/Laz22y/LazyForza/releases/latest
DefaultDirName={localappdata}\Programs\LazyForza
DefaultGroupName=LazyForza
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=LazyForza-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\LazyForza.App\Assets\LazyForza.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousLanguage=yes
VersionInfoVersion={#NumericVersion}
VersionInfoCompany=LazyForza
VersionInfoDescription=LazyForza Setup
VersionInfoProductName=LazyForza
VersionInfoProductVersion={#NumericVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
chinesesimp.SetupWindowTitle=安装 - %1
chinesesimp.UninstallAppFullTitle=卸载 %1
chinesesimp.InformationTitle=提示
chinesesimp.ConfirmTitle=确认
chinesesimp.ErrorTitle=错误
chinesesimp.ExitSetupTitle=退出安装
chinesesimp.ExitSetupMessage=安装尚未完成。现在退出将不会安装程序。%n%n确定退出安装吗？
chinesesimp.ButtonBack=< 上一步(&B)
chinesesimp.ButtonNext=下一步(&N) >
chinesesimp.ButtonInstall=安装(&I)
chinesesimp.ButtonOK=确定
chinesesimp.ButtonCancel=取消
chinesesimp.ButtonYes=是(&Y)
chinesesimp.ButtonNo=否(&N)
chinesesimp.ButtonFinish=完成(&F)
chinesesimp.ButtonBrowse=浏览(&B)...
chinesesimp.ButtonWizardBrowse=浏览(&B)...
chinesesimp.SelectLanguageTitle=选择安装语言
chinesesimp.SelectLanguageLabel=选择安装过程中使用的语言。
chinesesimp.ClickNext=点击“下一步”继续，或点击“取消”退出安装。
chinesesimp.BrowseDialogTitle=选择文件夹
chinesesimp.WelcomeLabel1=欢迎使用 [name] 安装向导
chinesesimp.WelcomeLabel2=将在你的电脑上安装 [name/ver]。%n%n建议继续前关闭其他应用。
chinesesimp.WizardSelectDir=选择安装位置
chinesesimp.SelectDirDesc=[name] 将安装到哪里？
chinesesimp.SelectDirLabel3=安装程序会将 [name] 安装到下列文件夹。
chinesesimp.SelectDirBrowseLabel=点击“下一步”继续；如需更改位置，请点击“浏览”。
chinesesimp.WizardReady=准备安装
chinesesimp.ReadyLabel1=安装程序已准备好在你的电脑上安装 [name]。
chinesesimp.ReadyLabel2a=点击“安装”开始；如需检查或修改设置，请点击“上一步”。
chinesesimp.ReadyLabel2b=点击“安装”开始。
chinesesimp.ReadyMemoDir=安装位置：
chinesesimp.ReadyMemoGroup=开始菜单文件夹：
chinesesimp.WizardPreparing=准备安装
chinesesimp.PreparingDesc=正在准备安装 [name]。
chinesesimp.WizardInstalling=正在安装
chinesesimp.InstallingLabel=正在安装 [name]，请稍候。
chinesesimp.FinishedHeadingLabel=[name] 安装完成
chinesesimp.FinishedLabelNoIcons=[name] 已安装到你的电脑。
chinesesimp.FinishedLabel=[name] 已安装到你的电脑，可从开始菜单启动。
chinesesimp.ClickFinish=点击“完成”退出安装程序。

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LazyForza"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Registry]
Root: HKA; Subkey: "Software\Classes\.lfztelemetry"; ValueType: string; ValueName: ""; ValueData: "LazyForza.Telemetry"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\LazyForza.Telemetry"; ValueType: string; ValueName: ""; ValueData: "LazyForza Telemetry Recording"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\LazyForza.Telemetry\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\LazyForza.Telemetry\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" --open ""%1"""

Root: HKA; Subkey: "Software\Classes\.lfzlap"; ValueType: string; ValueName: ""; ValueData: "LazyForza.LapAnalysis"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\LazyForza.LapAnalysis"; ValueType: string; ValueName: ""; ValueData: "LazyForza Lap Analysis"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\LazyForza.LapAnalysis\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\LazyForza.LapAnalysis\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" --open ""%1"""

Root: HKA; Subkey: "Software\Classes\.lfzestate"; ValueType: string; ValueName: ""; ValueData: "LazyForza.EstateTrack"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\LazyForza.EstateTrack"; ValueType: string; ValueName: ""; ValueData: "LazyForza Estate Track"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\LazyForza.EstateTrack\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\LazyForza.EstateTrack\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" --open ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,LazyForza}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
