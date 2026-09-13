; netHEmusic —— Inno Setup 安装脚本（中文向导 / 默认装到非系统盘 / 自签名证书 / 防火墙规则）
#define AppName "netHEmusic"
#define AppVer "26.9.13.1"
#define AppPub "Laohehehe"
#define AppExe "NetHEmusicCP.exe"

[Setup]
AppId={{8F3C1A24-6B7E-4C9D-9E15-2A7D5B4C1E01}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher={#AppPub}
AppPublisherURL=https://github.com/Laohehehe/netHEmusic
AppSupportURL=https://github.com/Laohehehe/netHEmusic
DefaultDirName=D:\Program Files\netHEmusic
DefaultGroupName=netHEmusic
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=netHEmusic_Setup_{#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\resources\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVer}
VersionInfoCompany={#AppPub}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "firewall"; Description: "为程序添加防火墙放行规则（Laohehehe）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "..\build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\netHEmusic"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\卸载 netHEmusic"; Filename: "{uninstallexe}"
Name: "{autodesktop}\netHEmusic"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; 安装自签名证书（当前用户 Root + 受信任的发布者），装完可用 certmgr.msc 查看
Filename: "{sys}\certutil.exe"; Parameters: "-f -user -addstore Root ""{app}\LaoheTeam.cer"""; Flags: runhidden; StatusMsg: "正在安装自签名证书…"
Filename: "{sys}\certutil.exe"; Parameters: "-f -user -addstore TrustedPublisher ""{app}\LaoheTeam.cer"""; Flags: runhidden
; 防火墙放行规则（名字固定为 Laohehehe，卸载时删除）
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Laohehehe"" dir=out program=""{app}\{#AppExe}"" action=allow enable=yes"; Flags: runhidden; Tasks: firewall; StatusMsg: "正在添加防火墙规则…"
; 安装完成后启动
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Laohehehe"""; Flags: runhidden; RunOnceId: "DelFirewallRule"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore Root LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertRoot"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore TrustedPublisher LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertTP"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  // 先结束正在运行的实例，避免覆盖安装时文件被占用
  Exec('taskkill.exe', '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
  Result := '';
end;