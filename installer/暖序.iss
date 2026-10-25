; ============================================================
;  暖序 Windows 安装包脚本（Inno Setup 6.5+）
;  编译： ISCC.exe /Qp installer\暖序.iss
;  编码： 必须 UTF-8 无 BOM（有 BOM 时 Inno 会把 #define 前的 BOM 当成非法字符）
;  前置： ..\dist 由 dotnet publish 生成；redist\ 放 WebView2 引导器
; ============================================================

#define MyAppName       "暖序"
; 版本号由 build-installer.ps1 从 csproj 读出后通过 /DMyAppVersion= 传入，
; 这里的默认值只在直接手敲 ISCC 时兜底，避免两处版本号写岔
#ifndef MyAppVersion
  #define MyAppVersion    "1.0.0"
#endif
#define MyAppExeName    "暖序.exe"
#define MyAppPublisher  "暖序"
; AppId 一旦发布就不能改，否则新旧版本会被系统当成两个不同的软件
#define MyAppId         "{{9C1F2A54-7E3B-4C8D-9A21-5B6E7F80C3D1}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 控制面板「应用和功能」里的反馈入口。有真实网址再打开这三行——
; 指向不存在的域名比不填更糟，用户点进去只会看到错误页
;AppPublisherURL=https://你的站点/
;AppSupportURL=https://你的站点/help
;AppUpdatesURL=https://你的站点/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; 开始菜单目录不让用户选，少一步
DisableProgramGroupPage=yes
AllowNoIcons=yes

; ---- 核心：安装向导开头让用户选「仅为我」/「为所有用户」----
; lowest = 默认按当前用户装，不弹 UAC；dialog 允许用户改选为所有用户（此时才提权）
;
; 必须同时给 commandline：Inno 的 /CURRENTUSER、/ALLUSERS 只在
; PrivilegesRequiredOverridesAllowed 里含 commandline 时才生效。
; 只写 dialog 的话，命令行传 /CURRENTUSER 会被静默忽略，
; 而「选择安装模式」那一页又不受 /SILENT 抑制——脚本化安装会永远卡在那一页等鼠标点。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

; 只支持 64 位（x64compatible 含 ARM64 上的 x64 模拟）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 8 要求 Win10 1607+，WebView2 要求 1803+，取 1809 保守值
MinVersion=10.0.17763

; ---- 产物 ----
OutputDir=..\release
OutputBaseFilename=暖序-{#MyAppVersion}-安装程序
SetupIconFile=..\NuanXu\app.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

; ---- 压缩：146MB 载荷 LZMA2 约压到 33%，出包 50~55MB ----
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

WizardStyle=modern
; 卸载段用到了 {localappdata}，关掉「用户区域」警告
UsedUserAreasWarning=no
; 暖序是托盘常驻程序，点 X 只收进托盘、不退出。
; 所以不能用 CloseApplications + AppMutex 那套：安装器发的 WM_CLOSE 会被程序自己取消，
; 用户只会看到「无法关闭程序」，然后强制结束进程——屏幕色温就留在暖色上复位不了。
; 改为由 [Code] 里的 PrepareToInstall 通过命名事件请它优雅退出。
CloseApplications=no
RestartApplications=no

[Languages]
; 本地存一份，不受 Inno Setup 版本升级影响
Name: "chinesesimp"; MessagesFile: "{#SourcePath}ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 整个 dist 一次性拷贝，靠 Excludes 剔除用不上的部分：
;   13 个语言卫星目录（约 15MB）、只给 32 位/ARM 用的 WebView2 loader、pdb、开发机跑出来的缓存
Source: "..\dist\*"; DestDir: "{app}"; \
  Excludes: "*.pdb,*.xml,暖序.exe.WebView2\*,cs\*,de\*,es\*,fr\*,it\*,ja\*,ko\*,pl\*,pt-BR\*,ru\*,tr\*,zh-Hant\*,\runtimes\win-x86\*,\runtimes\win-arm64\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; WebView2 在线引导器：只在目标机器缺运行时才会被执行
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
; autoprograms / autodesktop 会自动跟随安装模式（用户级 / 全局）
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 缺 WebView2 才装。引导器约 1.8MB，联网下载约 150MB，静默安装
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "正在安装 Microsoft Edge WebView2 运行时（需联网，约 150MB，请稍候）…"; \
  Flags: runhidden waituntilterminated; Check: NeedWebView2; AfterInstall: WebView2InstallDone

Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[Code]
const
  { WebView2 Runtime 的 EdgeUpdate 产品 GUID，微软官方文档给定，不要改 }
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  { 与 Program.cs 里的 Program.MutexName / ExitRequest.EventName 严格对应。
    Local\ 命名空间在同一登录会话内共享，即使用户选「为所有用户安装」（提权）也仍然命中。 }
  AppMutexName  = 'Local\NuanXu.SingleInstance';
  AppExitEvent  = 'Local\NuanXu.Exit';
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE        = $00100000;

{ 请求已在运行的暖序优雅退出。它收到事件后会先复位伽马表再关窗，
  所以屏幕不会停在暖色上。 }
function OpenEventW(dwDesiredAccess: Cardinal; bInheritHandle: LongBool; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function OpenMutexW(dwDesiredAccess: Cardinal; bInheritHandle: LongBool; lpName: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): LongBool;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';

{ 用互斥体探活：对象随进程销毁自动消失，比按进程名找更可靠 }
function IsAppRunning: Boolean;
var
  H: THandle;
begin
  H := OpenMutexW(SYNCHRONIZE, False, AppMutexName);
  Result := H <> 0;
  if Result then
    CloseHandle(H);
end;

{ 发退出信号并轮询等待，返回 True 表示已确认进程退出。
  没在运行时直接返回 True。 }
function RequestAppExit(TimeoutMs: Integer): Boolean;
var
  H: THandle;
  Waited: Integer;
begin
  if not IsAppRunning then
  begin
    Result := True;
    Exit;
  end;

  H := OpenEventW(EVENT_MODIFY_STATE, False, AppExitEvent);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
  end;

  Waited := 0;
  while (Waited < TimeoutMs) and IsAppRunning do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;
  Result := not IsAppRunning;
end;

{ 退出事件是否存在。1.8.0 之前的版本只有互斥体、没有事件，
  用来区分「新版没响应」和「旧版根本不支持自动退出」，好给出不同提示 }
function HasExitEvent: Boolean;
var
  H: THandle;
begin
  H := OpenEventW(EVENT_MODIFY_STATE, False, AppExitEvent);
  Result := H <> 0;
  if Result then
    CloseHandle(H);
end;

{ 拷文件之前先请它退出。退不掉就明确告诉用户怎么办，
  而不是让用户撞上「文件正在使用」这种看不懂的报错。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  if RequestAppExit(15000) then
    Exit;

  { 刚好在超时那一刻退掉了，别误报 }
  if not IsAppRunning then
    Exit;

  if HasExitEvent then
    Result := '暖序仍在运行，且未响应退出请求。' + #13#10 + #13#10 +
              '请右键点击任务栏托盘里的暖序图标，选择「退出暖序」，' + #13#10 +
              '然后点击「下一步」重试。'
  else
    { 1.7.x 及更早的版本没有退出事件，只能手动关。
      从 1.8.0 起升级不再需要这一步。 }
    Result := '检测到 1.8.0 之前的旧版暖序正在运行。' + #13#10 + #13#10 +
              '旧版不支持被安装程序自动关闭，请右键点击任务栏托盘里的暖序图标，' + #13#10 +
              '选择「退出暖序」（退出时它会自动把屏幕色温恢复正常），' + #13#10 +
              '然后点击「下一步」重试。以后从 1.8.0 升级不会再需要这一步。';
end;

{ 判断 pv 是不是一个"真"版本号。
  空串 / '0.0.0.0' 都表示"键存在但运行时没装"（卸载残留），不能算已安装。
  只要出现 1~9 的任意数字就算装了。 }
function IsRealVersion(const V: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to Length(V) do
    if (V[I] >= '1') and (V[I] <= '9') then
    begin
      Result := True;
      Exit;
    end;
end;

function ReadPv(RootKey: Integer; const SubKey: string; var Pv: string): Boolean;
begin
  Result := False;
  if not RegQueryStringValue(RootKey, SubKey, 'pv', Pv) then
    Exit;
  Result := IsRealVersion(Pv);
end;

{ 返回已安装的 WebView2 版本；未安装返回空串。
  依次覆盖：HKLM 原生视图 / HKLM 的 WOW6432Node（最常见） / HKCU（用户级安装）。
  脚本跑在 64 位安装模式，HKLM 是 64 位视图，所以要显式写 WOW6432Node 才能命中 32 位视图。 }
function GetWebView2Version: string;
var
  Pv: string;
begin
  Result := '';

  if ReadPv(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId, Pv) then
  begin
    Result := Pv;
    Exit;
  end;

  if ReadPv(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId, Pv) then
  begin
    Result := Pv;
    Exit;
  end;

  if ReadPv(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId, Pv) then
  begin
    Result := Pv;
    Exit;
  end;
end;

{ [Run] 的 Check：只有缺运行时才执行引导器 }
function NeedWebView2: Boolean;
begin
  Result := GetWebView2Version = '';
end;

{ 引导器跑完再复查一次；仍然没有就提示，但绝不阻断安装 }
procedure WebView2InstallDone;
begin
  if GetWebView2Version = '' then
    SuppressibleMsgBox(
      '未能自动安装 Microsoft Edge WebView2 运行时。' + #13#10 + #13#10 +
      '常见原因是这台机器没有联网，或系统策略禁止安装。' + #13#10 +
      '暖序仍会正常安装完成，但首次启动会提示缺少运行时。' + #13#10 + #13#10 +
      '请手动下载安装后再打开暖序：' + #13#10 +
      'https://go.microsoft.com/fwlink/p/?LinkId=2124703',
      mbInformation, MB_OK, IDOK);
end;

{ 升级时修正开机自启里的路径。
  用户若从 per-user 安装改装到 Program Files（或反过来），注册表里的旧路径会指向
  一个还在、但已经过期的 exe —— 开机会启动旧版本，比启动失败更难发现。
  只在「值已存在」时改写，保留用户「已开启」的意图，绝不主动创建。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
  Expected: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
       'NuanXu', Existing) then Exit;
  Expected := '"' + ExpandConstant('{app}\暖序.exe') + '" --autostart';
  if CompareText(Existing, Expected) = 0 then Exit;
  RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
    'NuanXu', Expected);
end;

{ 卸载时清理 WebView2 缓存（纯缓存，约 80MB）与伽马快照，
  但保留 settings.json。
  用 DelTree 的忽略错误模式，避免程序没退干净时弹「无法删除文件」的报错框。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I: Integer;
begin
  { TUninstallStep 只有三个合法值：usAppMutexCheck / usUninstall / usPostUninstall。
    没有 usPreUninstall 之类的常量，写错名字 ISCC 会直接报 Unknown identifier。

    退出请求必须放在 usAppMutexCheck：卸载器在 usUninstall 之前就会检查
    「程序是否正在运行」，命中就弹 UninstallAppRunningError。
    在 /SILENT + /SUPPRESSMSGBOXES 下那个框默认选「取消」，整个卸载直接中止
    （实测退出码 1、日志写 "Defaulting to Cancel for suppressed message box"）。
    放到 usUninstall 就太晚了——检查已经弹过了。 }
  if CurUninstallStep = usAppMutexCheck then
    { 先请程序退出并复位伽马表，否则卸载完屏幕会停在暖色上。
      不阻断卸载：超时就靠下面的 DelTree 忽略错误模式兜底 }
    RequestAppExit(15000);

  if CurUninstallStep = usPostUninstall then
  begin
    { 开机自启写在 HKCU 的 Run 键里，不在 [Registry] 段，卸载器不会自动清。
      必须自己删，否则卸载后每次开机都有一条指向已删目录的死项。
      顺带删掉任务管理器「启动」页的禁用标记（StartupApproved），别留孤儿值。 }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NuanXu');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'NuanXu');
    { WebView2 会拉起 6 个左右的 msedgewebview2.exe 子进程。主程序被 RequestAppExit
      关掉之后，这些子进程还要几秒才散，此时删缓存会因文件占用失败——而 DelTree 传了
      IgnoreErrors=True，失败是静默的，结果就是 80MB 缓存永远留在用户机器上。
      实测：单次删除 + Sleep(3000) 仍会残留（子进程退出时间不确定），
      必须循环重试；每一轮删不掉就再等 1 秒，最多 10 轮。 }
    for I := 1 to 10 do
    begin
      DelTree(ExpandConstant('{localappdata}\Nuánxù\WebView2'), True, True, True);
      if not DirExists(ExpandConstant('{localappdata}\Nuánxù\WebView2')) then
        Break;
      Sleep(1000);
    end;
    // 伽马快照与脏退出标记是本程序的工作文件，卸载后没有意义。
    // 必须连 .bak 一起删：AtomicFile 用 File.Replace 写入，每次都会留一份 .bak。
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\gamma-original.json'));
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\gamma-original.json.bak'));
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\session.lock'));
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\diag.txt'));
    // 日志同理：排障用完就没用了，留着只会在用户机器上堆 1MB 的陈旧记录。
    // settings.json 与 app.log 不同——那是用户偏好，卸载必须保留，下次装回来还在。
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\app.log'));
    DeleteFile(ExpandConstant('{localappdata}\Nuánxù\app.log.1'));
    // 父目录只有在 settings.json 也不存在（即已被用户删掉）时才会真的被删
    RemoveDir(ExpandConstant('{localappdata}\Nuánxù'));
    // 兼容旧版本把缓存写在安装目录里的情况
    DelTree(ExpandConstant('{app}\暖序.exe.WebView2'), True, True, True);
  end;
end;
