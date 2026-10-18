; ============================================================
;  CNM 安装包（Inno Setup 7）
;  编译： ISCC.exe CNM.iss
;  产物： ..\dist\CNM-Setup-<版本>.exe
; ============================================================

#define AppName "CNM"
#define AppVer "0.2.0"
#define AppPub "Chemingitch"

[Setup]
AppId={{8F2A7C41-5E93-4B6D-9A17-3C5D8E7B4A20}
AppName={#AppName}
AppVerName={#AppName} {#AppVer}（Chemingitch China Model）
AppVersion={#AppVer}
AppPublisher={#AppPub}
DefaultDirName={autopf}\CNM
DefaultGroupName=CNM
UninstallDisplayName=CNM（本机模型 + 云端中转）
UninstallDisplayIcon={app}\CNM.exe
OutputDir=..\dist
OutputBaseFilename=CNM-Setup-{#AppVer}
SetupIconFile=assets\cnm.ico
Compression=lzma2/max
SolidCompression=yes
; 干净克制：只换 logo 图，页面底色/控件全用 Inno 自己的亮暗配色（跟随系统，最和谐）
WizardStyle=modern dynamic
WizardImageFile=assets\big-light-202x386.png,assets\big-light-336x643.png,assets\big-light-430x824.png
WizardImageFileDynamicDark=assets\big-dark-202x386.png,assets\big-dark-336x643.png,assets\big-dark-430x824.png
WizardSmallImageFile=assets\small-light-58x58.png,assets\small-light-97x97.png,assets\small-light-124x124.png
WizardSmallImageFileDynamicDark=assets\small-dark-58x58.png,assets\small-dark-97x97.png,assets\small-dark-124x124.png
WizardSizePercent=110
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
DisableProgramGroupPage=yes
DisableReadyPage=no
AllowNoIcons=yes
InfoBeforeFile=assets\info-before.txt
VersionInfoVersion={#AppVer}.0
VersionInfoCompany={#AppPub}
VersionInfoDescription=CNM 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVer}
VersionInfoCopyright=Copyright (C) 2026 {#AppPub}
; CNM 自己会开一个同名互斥体：升级/卸载前先让用户把程序关掉，免得文件被占用
AppMutex=CNM.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "lang\ChineseSimplified.isl"

[Tasks]
Name: "engine"; Description: "装上推理引擎（Vulkan 版，约 87 MB；本机跑模型要用。不勾也行 —— 装完在「设置 → 推理引擎」里能一键下载，还能挑 CUDA / ROCm / CPU 版）"; GroupDescription: "组件："; Flags: checkedonce
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "firewall"; Description: "在 Windows 防火墙里放行 1234 端口（局域网里别的设备才能连上）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
; 引擎单独一条：不勾「推理引擎」就不打进包里，安装包小 30 MB 左右
Source: "..\dist\CNM\*"; DestDir: "{app}"; Excludes: "engine\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\CNM\engine\*"; DestDir: "{app}\engine"; Tasks: engine; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
; WebView2 引导安装器：只在机器上没有 WebView2 时才解出来用，平时不落地
Source: "assets\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\CNM"; Filename: "{app}\CNM.exe"; Comment: "本机模型 + 云端中转，一条 OpenAI 兼容服务"
Name: "{group}\CNM 使用说明"; Filename: "{app}\README.md"
Name: "{autodesktop}\CNM"; Filename: "{app}\CNM.exe"; Comment: "本机模型 + 云端中转"; Tasks: desktopicon

[Run]
Filename: "{app}\CNM.exe"; Description: "立即运行 CNM"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\README.md"; Description: "看一眼使用说明（README）"; Flags: postinstall nowait skipifsilent unchecked runascurrentuser
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""CNM (TCP 1234)"" dir=in action=allow protocol=TCP localport=1234 program=""{app}\CNM.exe"" enable=yes profile=any"; StatusMsg: "正在让 Windows 防火墙放行 1234 端口……"; Flags: runhidden; Tasks: firewall

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""CNM (TCP 1234)"""; RunOnceId: "DelFirewallRule"; Flags: runhidden

[Messages]
SetupWindowTitle=CNM 安装程序 - %1
SetupAppRunningError=检测到 CNM 正在运行。%n%n请右键任务栏右下角托盘里的 CNM 图标 → 退出（也可以先把窗口关掉），然后点「确定」继续。
UninstallAppRunningError=检测到 CNM 正在运行。%n%n请先从托盘里退出 CNM，然后点「确定」继续卸载。
WelcomeLabel1=欢迎安装 CNM
WelcomeLabel2=接下来会把 CNM（Chemingitch China Model）装到这台电脑上。%n%nCNM 把本机的 GGUF 模型和你买的云端中转站，统一成一条 OpenAI 兼容服务——任何 agent 只要填 http://127.0.0.1:1234/v1 就能用，本机模型和云端模型在它眼里是一回事。%n%n点「下一步」开始，整个安装大概一分钟。
SelectDirDesc=CNM 自己装在哪儿？
SelectDirLabel3=CNM 程序装在这个目录（约 250 MB）。%n本机模型不装在这里，下一步单独选。
SelectTasksDesc=再顺手做点什么？
SelectTasksLabel2=推理引擎和附加任务，按需要勾选：
ReadyLabel1=准备就绪
ReadyLabel2a=点「安装」就开始复制文件；想再核对一下，点「上一步」。
PreparingDesc=正在准备安装……
InstallingLabel=正在把 CNM 装到你的电脑上……
FinishedHeadingLabel=CNM 装好了
FinishedLabel=CNM 已经装到这台电脑上。%n%n服务地址：http://127.0.0.1:1234/v1%n（局域网里其它设备要用它，就把 127.0.0.1 换成这台电脑的 IP）%n%nAPI Key 在「设置」页里，能看、能改、能重新生成。%n第一次用建议：到「模型库 → 在线浏览」下一个 GGUF，或者到「服务与日志 → 守望者」把中转站接进来。
InfoBeforeLabel=动手之前，请先扫一眼下面这几件事：

[Code]
var
  ModelsDirPage: TInputDirWizardPage;
  WebView2Ok: Boolean;

{ ---------------- 小工具 ---------------- }

function SettingsJsonPath: String;
begin
  Result := ExpandConstant('{userappdata}\CNM\settings.json');
end;

function NewApiKey: String;
var
  Seed: String;
begin
  Seed := GetDateTimeString('yyyymmddhhnnsszzz', #0, #0) + '|' +
          GetComputerNameString + '|' + IntToStr(Random(1000000000));
  Result := 'sk-chem-' + Copy(GetMD5OfString(Seed), 1, 32);
end;

{ JSON 字符串值 → 真值（CNM 写的配置里反斜杠是 \\ 转义过的） }
function JsonStringValue(const Json, Key: String): String;
var
  P, Q: Integer;
begin
  Result := '';
  P := Pos('"' + Key + '"', Json);
  if P = 0 then Exit;
  P := P + Length(Key) + 2;
  while (P <= Length(Json)) and (Json[P] <> '"') do Inc(P);
  if P > Length(Json) then Exit;
  Inc(P);
  Q := P;
  while (Q <= Length(Json)) and (Json[Q] <> '"') do Inc(Q);
  if Q > Length(Json) then Exit;
  Result := Copy(Json, P, Q - P);
  StringChangeEx(Result, '\\', '\', True);
  StringChangeEx(Result, '\"', '"', True);
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function ExistingModelsDir: String;
var
  Lines: TArrayOfString;
  I: Integer;
  All: String;
begin
  Result := '';
  if not FileExists(SettingsJsonPath) then Exit;
  { CNM 写的是 UTF-8 的 JSON；LoadStringsFromFile 认 BOM 也认无 BOM 的 UTF-8 }
  if not LoadStringsFromFile(SettingsJsonPath, Lines) then Exit;
  All := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    All := All + Lines[I] + #10;
  Result := JsonStringValue(All, 'ModelsDir');
end;

{ 本机没有配置时，默认把模型放在用户目录下（一定能写、不会踩权限坑） }
function BestModelsDir: String;
begin
  Result := ExpandConstant('{userprofile}\CNM\models');
end;

function HasWebView2: Boolean;
var
  Pv, Sub: String;
begin
  Sub := '\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node' + Sub, 'pv', Pv) and
    (Pv <> '') and (Pv <> '0.0.0.0');
  if not Result then
    Result :=
      RegQueryStringValue(HKCU, 'SOFTWARE' + Sub, 'pv', Pv) and
      (Pv <> '') and (Pv <> '0.0.0.0');
end;

{ CNM 第一次启动时自己也会写这份配置（连 ApiKey 一起）——我们只在本机还没有配置时写，
  有配置就一个字节都不动，免得上游地址和 Key 被覆盖。 }
function WriteFreshSettings(const Dir: String): Boolean;
var
  Lines: TArrayOfString;
begin
  Result := False;
  if FileExists(SettingsJsonPath) then
  begin
    Result := True;      { 已有配置：什么都不做，算成功 }
    Exit;
  end;
  if not ForceDirectories(ExpandConstant('{userappdata}\CNM')) then Exit;
  SetArrayLength(Lines, 4);
  Lines[0] := '{';
  Lines[1] := '  "ModelsDir": "' + JsonEscape(Dir) + '",';
  Lines[2] := '  "ApiKey": "' + NewApiKey + '"';
  Lines[3] := '}';
  { UTF-8 存档：CNM 用 System.Text.Json 读，认 BOM }
  Result := SaveStringsToUTF8File(SettingsJsonPath, Lines, False);
end;

{ ---------------- 向导 ---------------- }

procedure InitializeWizard;
var
  Existing, Tip: String;
begin
  WebView2Ok := HasWebView2;
  Existing := ExistingModelsDir;

  if Existing <> '' then
  begin
    Tip := '这台电脑上已经有 CNM 配置了，安装不会改动它 —— 上游地址、Key、用量都存在 %APPDATA%\CNM，' +
           '下面只是把当前的模型目录显示出来。';
    ModelsDirPage := CreateInputDirPage(wpSelectTasks,
      '模型存放目录', '本机 GGUF 模型放在这个目录里。', Tip, False, '');
    ModelsDirPage.Add('模型目录：');
    ModelsDirPage.Values[0] := Existing;
  end
  else
  begin
    Tip := '模型是几个 GB 起步的大文件，建议挑一个空间充裕的盘；装完之后在「设置 → 模型目录」里随时能改。';
    ModelsDirPage := CreateInputDirPage(wpSelectTasks,
      '模型存放目录', '本机 GGUF 模型放在这个目录里。', Tip, False, '');
    ModelsDirPage.Add('模型目录：');
    ModelsDirPage.Values[0] := BestModelsDir;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
  FreeB, TotalB: Int64;
begin
  Result := True;
  if CurPageID <> ModelsDirPage.ID then Exit;

  Dir := Trim(ModelsDirPage.Values[0]);
  if Dir = '' then
  begin
    MsgBox('请填一个模型目录（比如 D:\CNM\models）。', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if Pos(':\', Dir) = 0 then
  begin
    MsgBox('请填完整的路径（带盘符），比如 D:\CNM\models。', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  { 空间太小提醒一下：一个 7B 的 4bit 模型都要 4～5 GB }
  if GetSpaceOnDisk64(Copy(Dir, 1, 3), FreeB, TotalB) and (FreeB < 8589934592) then
  begin
    if MsgBox(Dir + ' 所在的分区只剩 ' + IntToStr(FreeB div 1073741824) + ' GB，' +
              '一个大一点儿的模型可能就装不下了。' + #13#10#13#10 + '还是用它吗？',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function UpdateReadyMemo(const Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if MemoDirInfo <> '' then Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoGroupInfo <> '' then Result := Result + MemoGroupInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + '模型目录：' + NewLine + Space + ModelsDirPage.Values[0] + NewLine + NewLine;
  if IsTaskSelected('engine') then
    Result := Result + '推理引擎：' + NewLine + Space + '装上 Vulkan 版（约 87 MB，开箱即用）' + NewLine + NewLine
  else
    Result := Result + '推理引擎：' + NewLine + Space + '不装 —— 装完在「设置 → 推理引擎（llama.cpp）」点「下载并安装引擎」，' +
              '可以挑 Vulkan / ROCm / CUDA / CPU 版（慢网或断线都能续传）' + NewLine + NewLine;
  if WebView2Ok then
    Result := Result + '微软 WebView2 运行时：' + NewLine + Space + '已检测到，不用额外装'
  else
    Result := Result + '微软 WebView2 运行时：' + NewLine + Space + '没检测到 —— 安装时会用自带的引导器帮你装（大约 1.6 MB，需要联网）';
end;

{ 装 WebView2（老系统 / LTSC 镜像上常缺，缺了 CNM 界面出不来） }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Rc: Integer;
  Dir: String;
begin
  Result := '';

  { 真的开始装了，才落模型目录：建目录 + 本机没有配置时写一份最小的 settings.json }
  Dir := Trim(ModelsDirPage.Values[0]);
  if Dir <> '' then
  begin
    if not ForceDirectories(Dir) then
      MsgBox('建不了模型目录：' + #13#10 + Dir + #13#10#13#10 +
             '（不影响安装，装完在「设置 → 模型目录」里改一下就行）', mbInformation, MB_OK)
    else
      WriteFreshSettings(Dir);
  end;

  if WebView2Ok then Exit;

  if MsgBox('这台电脑上没有检测到「微软 WebView2 运行时」，CNM 的界面需要它。' + #13#10#13#10 +
            '要用安装包里自带的引导器装一下吗？（约 1.6 MB，需要联网，大概半分钟）',
            mbConfirmation, MB_YESNO) = IDNO then
  begin
    WebView2Ok := True;    { 用户自己决定，别再唠叨 }
    Exit;
  end;

  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
          SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 0) then
  begin
    WebView2Ok := True;
    MsgBox('WebView2 运行时装好了。', mbInformation, MB_OK);
  end
  else
    MsgBox('WebView2 没装上（可能没联网）。' + #13#10#13#10 +
           'CNM 会照常安装，但界面需要 WebView2 才能显示 —— 装完可以到微软官网搜 ' +
           '「WebView2 Runtime」装一下（Win11 和较新的 Win10 一般都自带）。',
           mbError, MB_OK);
end;

{ 卸载时问一句：配置和缓存（上游地址、Key、用量、logo 缓存）要不要一起删 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if not UninstallSilent then
    begin
      if MsgBox('要不连配置一起删掉吗？' + #13#10#13#10 +
                '包含：上游中转站的地址和 Key、用量统计、logo 缓存（都在 %APPDATA%\CNM）。' + #13#10 +
                '想留着下次装回来还能接着用，就选「否」。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\CNM'), True, True, True);
    end;
  end;
end;
