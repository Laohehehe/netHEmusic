; netHEmusic —— Inno Setup 安装脚本（中文向导 / 默认装到非系统盘 / 自签名证书 / 防火墙规则）
#define AppName "netHEmusic"
#define AppVer "26.9.25.5"
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
; 中文语言包 Inno 官方安装包并不带（属于社区翻译），原来写 compiler:Languages\... 只在「本机装过这个语言包」时能用，
; 换台机器（比如 GitHub Actions）编译会直接报 Couldn't open include file。
; 所以随仓库带一份：installer\Languages\ChineseSimplified.isl
; 来源：Inno Setup 非官方翻译，维护者 Zhenghan Yang (Kira)
;       https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
Name: "chinese"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "firewall"; Description: "为程序添加防火墙放行规则（Laohehehe）"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "trustcert"; Description: "把本软件的签名证书装进【本机】受信任存储（以后更新/卸载的 UAC 不再显示「未知发布者」）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "..\build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 证书单独补一份：payload 里不一定有最新那张。用 #if 兜底，文件不在也不会让编译失败。
#if FileExists(AddBackslash(SourcePath) + "..\resources\LaoheTeam.cer")
Source: "..\resources\LaoheTeam.cer"; DestDir: "{app}"; Flags: ignoreversion
#endif
#if FileExists(AddBackslash(SourcePath) + "..\resources\Laohehehe.cer")
Source: "..\resources\Laohehehe.cer"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\netHEmusic"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\卸载 netHEmusic"; Filename: "{uninstallexe}"
Name: "{autodesktop}\netHEmusic"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; 证书必须装进【本机】存储：UAC 的同意窗口是 consent.exe 以 SYSTEM 身份弹的，只认 LocalMachine。
; 以前只装 -user，所以自己机器上签名校验是好的，别人机器上的 UAC 却永远是「未知发布者」。
; 安装器本身就有管理员权限（PrivilegesRequired=admin），所以这里写得进去。
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore Root ""{app}\LaoheTeam.cer"""; Flags: runhidden; Tasks: trustcert; StatusMsg: "正在把签名证书装进本机受信任的根证书颁发机构…"
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore TrustedPublisher ""{app}\LaoheTeam.cer"""; Flags: runhidden; Tasks: trustcert
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore TrustedPublisher ""{app}\Laohehehe.cer"""; Flags: runhidden; Tasks: trustcert
; 当前用户存储也来一份（本地开发时 Get-AuthenticodeSignature 走的是这份）
Filename: "{sys}\certutil.exe"; Parameters: "-f -user -addstore Root ""{app}\LaoheTeam.cer"""; Flags: runhidden; Tasks: trustcert
Filename: "{sys}\certutil.exe"; Parameters: "-f -user -addstore TrustedPublisher ""{app}\LaoheTeam.cer"""; Flags: runhidden; Tasks: trustcert
; 防火墙放行规则（名字固定为 Laohehehe，卸载时删除）
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Laohehehe"" dir=out program=""{app}\{#AppExe}"" action=allow enable=yes"; Flags: runhidden; Tasks: firewall; StatusMsg: "正在添加防火墙规则…"
; 安装完成后启动
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Laohehehe"""; Flags: runhidden; RunOnceId: "DelFirewallRule"
; 本机存储里的也一并撤掉（卸载要卸载干净），当前用户那份照旧
Filename: "{sys}\certutil.exe"; Parameters: "-delstore Root LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertRootMachine"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPublisher LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertTPMachine"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPublisher Laohehehe"; Flags: runhidden; RunOnceId: "DelCertLeafTPMachine"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore Root LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertRoot"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore TrustedPublisher LaoheTeam.top"; Flags: runhidden; RunOnceId: "DelCertTP"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore TrustedPublisher Laohehehe"; Flags: runhidden; RunOnceId: "DelCertLeafTP"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  // 先结束正在运行的实例，避免覆盖安装时文件被占用
  Exec('taskkill.exe', '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
  Result := '';
end;