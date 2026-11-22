; Simosa BRM 操船模擬器 — Windows 安裝程式(Inno Setup 6.3+ 腳本;規劃書第 5.5 節「船上部署」、第 11.2 節資料夾結構、第 12.1 節 M4 交付「安裝程式」;
; 安裝步驟與驗收見 docs\ops\安裝與啟動.md 第 2.8 節)。
; 編譯(由 tools\build-release.ps1 -Installer 呼叫;也可手動):
;   ISCC.exe /DAppVersion=<版本> /DSourceDir=<build\release\<版本>> /DOutputDir=<build\release\installer> tools\installer\SimosaBRM.iss
;   → <OutputDir>\SimosaBRM-Setup-<版本>.exe(/DDirectLinkSubnet=192.168.77.0/24 可改防火牆允許的直連網段)
; 安裝內容 = 發行版目錄(含 manifest.json;tools\build-release.* 產生並 release verify 通過者)原樣複製到安裝根目錄
; (安裝後與使用中的核對用 simosa-pkg release verify --dir . --installed:執行期資料不算多出、本機修改的 config\ 只列提醒):
;   預設 D:\Simosa BRM,沒有 D: 時 G:\Simosa BRM(可改;不接受使用者資料夾 C:\Users\… 與 Program Files 之下:程式與紀錄、log、設定全部留在安裝根目錄內,不寫 C:\ 使用者資料)。
;   裝在系統磁碟(通常 C:)須明確確認(須經訓練負責人同意;靜默安裝加 /ALLOWSYSTEMDRIVE=1):守則為本機資料放 D: 或 G:。
; 另做:開始功能表/桌面捷徑(操船台外殼、教官站、停止)、Windows 防火牆入站規則(僅直連私有網段;全部設定檔,因直連無 DHCP 會被判為公用網路)、
;   WebView2 Runtime 離線安裝檔(tools\webview2\)存在且尚未安裝時一併執行、.NET 8 執行階段離線安裝檔(tools\dotnet\)存在時一併執行。
; 重新安裝/升級:[InstallDelete] 在安裝前先清空程式目錄 app\ 與 tools\updater\(舊版留下、新版沒有的程式檔;安裝後這兩個資料夾與發行版完全一致,
;   release verify --installed 不會把舊檔列為「多出的檔案」);config\、data\、state\、store\、build\(設定、紀錄、執行期資料)不動。安裝前先以 stop-all.ps1 結束全部程序。
; 解除安裝:只移除安裝程式放進去的檔案與 build\logs、build\tmp;保留 data\records、build\records、state\、store\、config\(練習紀錄與設定)
;   以及 build\webview2、build\browser(瀏覽器端資料:草稿、參與者資料、講評報告站的訓練紀錄)、build\downloads(從網頁下載的檔案)。
; 本腳本的靜態檢查:python3 -I tools\installer\check-iss.py tools\installer\SimosaBRM.iss(tools\run-ps-tests.sh 一併執行);編譯與安裝需 Windows 實機。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir AddBackslash(SourcePath) + "..\..\build\release\" + AppVersion
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\..\build\release\installer"
#endif
#ifndef DirectLinkSubnet
  #define DirectLinkSubnet "192.168.77.0/24"
#endif
#define AppName "Simosa BRM 操船模擬器"
#define AppPublisher "Simosa BRM 專案"
#define WebView2Installer "tools\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#if !FileExists(AddBackslash(SourceDir) + "manifest.json")
  #error 發行版目錄沒有 manifest.json:請先執行 tools\build-release.ps1 產生 build\release\<版本>\,或以 /DSourceDir= 指定
#endif

[Setup]
; AppId 固定(GUID;升級/重新安裝時以同一識別碼找到先前安裝位置)
AppId={{6B0C7F8E-3B7A-4A3F-9C5D-2B1E8A7D4F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
; 預設安裝位置由 [Code] GetDefaultDir 決定:固定磁碟的 D: → G: → 抽取式的 D: → G:(不是安裝程式所在的磁碟;光碟機不用)→ 系統磁碟根目錄下的 Simosa BRM(須明確確認);使用者可改(DisableDirPage=no)
DefaultDirName={code:GetDefaultDir}
DefaultGroupName=Simosa BRM
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
UsePreviousAppDir=yes
; 防火牆規則與 Runtime 安裝需要系統管理員
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=SimosaBRM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 解除安裝檔放安裝根目錄的 tools\uninstall\(不寫 C:\);simosa-pkg release verify(一般與 --installed 模式)會略過 unins*.exe/.dat
UninstallFilesDir={app}\tools\uninstall
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={sys}\WindowsPowerShell\v1.0\powershell.exe
CloseApplications=no
ChangesEnvironment=no
ShowLanguageDialog=no
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoCopyright={#AppPublisher}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
; Inno Setup 官方未附繁體中文翻譯;開發機若自行放入 Languages\ChineseTraditional.isl 則一併提供(安裝精靈文字;本腳本自訂訊息已為繁體中文)
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseTraditional.isl")
Name: "zh_TW"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑(操船台外殼、教官站、停止)"; GroupDescription: "捷徑:"
Name: "firewall"; Description: "登錄 Windows 防火牆入站規則(只允許直連私有網段 {#DirectLinkSubnet};TCP 8765–8768、8080、7300,UDP 7001、60001、10110–10112;涵蓋全部設定檔)"; GroupDescription: "網路(docs\ops\安裝與啟動.md 第 2.2 節):"
#if FileExists(AddBackslash(SourceDir) + WebView2Installer)
Name: "webview2"; Description: "安裝 Microsoft Edge WebView2 Runtime(離線安裝檔 tools\webview2\;已安裝時略過)"; GroupDescription: "執行階段(第 4.5 節):"
#endif

[Dirs]
; 資料資料夾標 uninsneveruninstall:解除安裝時不動(Inno Setup 預設會移除自己建立、已清空的資料夾);紀錄、設定、瀏覽器端資料一律保留
; 整個安裝根目錄讓一般使用者(船上帶領者/學員帳號)可寫:log、紀錄、state\、同步服務套用軟體更新包時替換 app\(docs\ops\安裝與啟動.md 第 2.1 節帳號)
Name: "{app}"; Permissions: users-modify
Name: "{app}\build\logs"
Name: "{app}\build\records"; Flags: uninsneveruninstall
Name: "{app}\build\webview2"; Flags: uninsneveruninstall
Name: "{app}\data\records"; Flags: uninsneveruninstall
Name: "{app}\state"; Flags: uninsneveruninstall
Name: "{app}\store"; Flags: uninsneveruninstall
Name: "{app}\config\trusted-keys"; Flags: uninsneveruninstall
Name: "{app}\config\device"; Flags: uninsneveruninstall

[InstallDelete]
; 重新安裝/升級時,安裝前先清空程式目錄:安裝後 app\ 與 tools\updater\ 才與發行版(manifest.json)完全一致。不清的話,舊版留下而新版沒有的檔案
; (每次建置檔名雜湊都不同的網頁資產、先前更新包新增的檔案)會被載入或被網頁伺服送出,release verify --installed 也會把它們列為
; 「程式目錄內多出的檔案」錯誤(docs\design\packaging-sync.md 第 5.1 節;覆蓋清單在重新安裝後同樣只認新的發行清單)。
; 只能列這兩個程式目錄(= simosa-pkg 的 InstalledPaths.ProgramPrefixes;check-iss.py 以白名單檢查、Tools.Packaging.Tests 對照):
; 不可動到 config\、data\、state\、store\、build\(設定、船模與情境、更新包狀態、紀錄庫、練習紀錄與瀏覽器端資料)。
; 全新安裝時這兩個資料夾不存在,不做任何事;執行中的檔案刪不掉,重新安裝前先以 stop-all.ps1 結束全部程序(含同步服務)。
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\tools\updater"

[Files]
; 發行版全部內容(manifest.json、README.txt、app\、tools\、data\、contracts\、docs\、packages\);config\ 另列。
; Excludes 的檔名樣式(\config\* 以外)必須與 simosa-pkg 的 ReleaseManifestBuilder.InstallerExcludedPatterns(src\Tools.Packaging\Release\ReleaseManifest.cs)
; 完全相同,兩邊要一起改(check-iss.py 對照):發行清單若列了這裡排除的檔案,安裝後 release verify --installed 會報「檔案缺少」。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\config\*,*.pdb,Thumbs.db"; Flags: recursesubdirs createallsubdirs ignoreversion
; 設定範例(ship-laptop.json、instructor-laptop.json、packaging.*.example.json、README.md):已存在(安裝時依機器修改過)者不覆寫、解除安裝不刪
Source: "{#SourceDir}\config\*"; DestDir: "{app}\config"; Flags: recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Simosa BRM 操船台(WebView2 外殼)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\start-ship-laptop.ps1"" -Shell"; WorkingDir: "{app}"; Comment: "依 config\ship-laptop.json 啟動核心、雷達、網頁、視景、同步服務,並以 kiosk 外殼開啟操船台(第 4.1、4.5 節)"
Name: "{group}\Simosa BRM 教官站(訪船直連)"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\start-instructor-laptop.ps1"" -CheckWifi"; WorkingDir: "{app}"; Comment: "檢查 Wi-Fi 已停用、連線船上核心並開啟教官站、雷達鏡像、講評報告(第 4.2 節)"
Name: "{group}\停止 Simosa BRM"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\stop-all.ps1"""; WorkingDir: "{app}"; Comment: "先寫停止檔讓核心寫完紀錄 footer,再結束全部程序"
Name: "{group}\安裝與啟動說明"; Filename: "{app}\docs\ops\安裝與啟動.md"
Name: "{group}\解除安裝 Simosa BRM"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Simosa BRM 操船台"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\start-ship-laptop.ps1"" -Shell"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autodesktop}\Simosa BRM 教官站"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\start-instructor-laptop.ps1"" -CheckWifi"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autodesktop}\停止 Simosa BRM"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\app\launch\stop-all.ps1"""; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
#if FileExists(AddBackslash(SourceDir) + WebView2Installer)
; WebView2 Runtime 離線安裝(Evergreen Standalone Installer x64;不需網際網路;第 4.5 節第 1 項);已安裝(登錄檔 pv)時略過
Filename: "{app}\{#WebView2Installer}"; Parameters: "/silent /install"; StatusMsg: "安裝 WebView2 Runtime(離線)…"; Tasks: webview2; Check: not WebView2RuntimeInstalled; Flags: waituntilterminated
#endif
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\app\launch\start-ship-laptop.ps1"" -CheckOnly"; WorkingDir: "{app}"; Description: "檢查找到的元件(start-ship-laptop.ps1 -CheckOnly;不啟動)"; Flags: postinstall nowait skipifsilent unchecked
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -Command ""Set-Location -LiteralPath '{app}'; dotnet tools\updater\simosa-pkg.dll release verify --dir . --installed"""; WorkingDir: "{app}"; Description: "核對安裝的程式檔(simosa-pkg release verify --installed;第 2.4 節)"; Flags: postinstall nowait skipifsilent unchecked

[UninstallDelete]
; 只清 log 與暫存;練習紀錄(build\records、data\records)、state\、store\、config\ 保留(解除安裝後提示路徑)。
; 瀏覽器端資料(build\webview2 外殼、build\browser 專屬 Edge 設定檔:草稿、參與者資料、訓練紀錄)與 build\downloads 也保留,不在此刪除。
Type: filesandordirs; Name: "{app}\build\logs"
Type: filesandordirs; Name: "{app}\build\tmp"

[Code]
const
  // WebView2 Runtime(Evergreen)的 EdgeUpdate 用戶端登錄鍵:pv 為已安裝版本(docs\ops\安裝與啟動.md 第 4.5 節第 1 項)
  WebView2ClientKey64 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2ClientKeyUser = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  DirectLinkSubnet = '{#DirectLinkSubnet}';
  RulePrefix = 'Simosa BRM ';
  RuleCount = 9;
  // GetDriveType 的回傳值(Win32 DRIVE_*)
  DRIVE_REMOVABLE = 2;
  DRIVE_FIXED = 3;

// Win32 GetDriveTypeW:磁碟種類(固定磁碟 / 抽取式 / 光碟機 / 網路磁碟…)
function GetDriveType(lpRootPathName: string): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

// 可當預設安裝位置的磁碟:固定磁碟(本機硬碟 / SSD;外接 USB 硬碟多數也回報為固定磁碟)。AllowRemovable 時另接受抽取式磁碟
//(隨身碟、記憶卡),但不接受安裝程式本身所在的磁碟(放安裝檔的隨身碟通常裝完就拔走)。光碟機、讀卡機的空槽、網路磁碟、RAM 磁碟一律不用
function UsableDrive(Root: string; AllowRemovable: Boolean): Boolean;
var
  T: Cardinal;
begin
  Result := False;
  if not DirExists(Root) then
    Exit;
  T := GetDriveType(Root);
  if T = DRIVE_FIXED then
    Result := True
  else if AllowRemovable and (T = DRIVE_REMOVABLE) then
    Result := CompareText(ExtractFileDrive(ExpandConstant('{src}')), Copy(Root, 1, 2)) <> 0;
end;

// 預設安裝位置(規劃書第 11.2 節;守則:本機資料放 D: 或 G:):先找固定磁碟的 D:、G:,其次抽取式的 D:、G:(不是安裝程式所在的那一個),
// 都沒有才是系統磁碟根目錄下的 Simosa BRM(仍不在使用者資料夾之下)— 此時與使用者自行改選系統磁碟時,NextButtonClick 會要求明確確認。
// 原本只看磁碟是否存在:D: 是光碟機或放安裝檔的隨身碟時也會被選為預設位置(第二十八輪審查建議)。重新安裝時沿用上次的位置(UsePreviousAppDir)
function GetDefaultDir(Param: string): string;
begin
  if UsableDrive('D:\', False) then
    Result := 'D:\Simosa BRM'
  else if UsableDrive('G:\', False) then
    Result := 'G:\Simosa BRM'
  else if UsableDrive('D:\', True) then
    Result := 'D:\Simosa BRM'
  else if UsableDrive('G:\', True) then
    Result := 'G:\Simosa BRM'
  else
    Result := ExpandConstant('{sd}\Simosa BRM');
end;

function WebView2RuntimeInstalled: Boolean;
var
  V: string;
begin
  Result := False;
  if RegQueryStringValue(HKLM, WebView2ClientKey64, 'pv', V) and (V <> '') and (V <> '0.0.0.0') then
    Result := True
  else if RegQueryStringValue(HKCU, WebView2ClientKeyUser, 'pv', V) and (V <> '') and (V <> '0.0.0.0') then
    Result := True;
end;

// .NET 8 共用執行階段是否存在(Family:Microsoft.NETCore.App 或 Microsoft.WindowsDesktop.App):看 DOTNET_ROOT 或 Program Files\dotnet\shared\<Family>\8.*
function DotNetRuntimePresent(const Family: string): Boolean;
var
  Root: string;
  FR: TFindRec;
begin
  Result := False;
  Root := GetEnv('DOTNET_ROOT');
  if Root = '' then
    Root := ExpandConstant('{commonpf}\dotnet');
  if FindFirst(AddBackslash(Root) + 'shared\' + Family + '\8.*', FR) then
  begin
    try
      Result := True;
    finally
      FindClose(FR);
    end;
  end;
end;

// 發行版 tools\dotnet\ 內若放了 .NET 8 離線安裝檔(岸上自 Microsoft 下載;不在儲存庫)則靜默安裝:Pattern 例 dotnet-runtime-8*-win-x64.exe
procedure InstallBundledRuntime(const Pattern, Family, Title: string);
var
  Dir: string;
  FR: TFindRec;
  R: Integer;
begin
  if DotNetRuntimePresent(Family) then
    Exit;
  Dir := ExpandConstant('{app}\tools\dotnet\');
  if FindFirst(Dir + Pattern, FR) then
  begin
    try
      Log('安裝 ' + Title + ':' + FR.Name);
      if not Exec(Dir + FR.Name, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, R) or ((R <> 0) and (R <> 3010)) then
        Log(Title + ' 安裝失敗(結束碼 ' + IntToStr(R) + ')');
    finally
      FindClose(FR);
    end;
  end;
end;

// 防火牆規則名稱與內容(與 docs\ops\安裝與啟動.md 第 2.2 節的 PowerShell 片段相同;remoteip 限直連私有網段、profile=any 涵蓋公用設定檔)
procedure GetRule(Index: Integer; var Name, Proto, Ports: string);
begin
  case Index of
    0: begin Name := '狀態 WebSocket'; Proto := 'TCP'; Ports := '8765'; end;
    1: begin Name := '雷達 WebSocket'; Proto := 'TCP'; Ports := '8766'; end;
    2: begin Name := 'VHF 角色扮演'; Proto := 'TCP'; Ports := '8767'; end;
    3: begin Name := '錄影上傳'; Proto := 'TCP'; Ports := '8768'; end;
    4: begin Name := '網頁'; Proto := 'TCP'; Ports := '8080'; end;
    5: begin Name := '直連同步'; Proto := 'TCP'; Ports := '7300'; end;
    6: begin Name := '狀態多播'; Proto := 'UDP'; Ports := '7001'; end;
    7: begin Name := 'NMEA 多播'; Proto := 'UDP'; Ports := '60001'; end;
    8: begin Name := 'NMEA 單播'; Proto := 'UDP'; Ports := '10110-10112'; end;
  end;
  Name := RulePrefix + Name;
end;

procedure RemoveFirewallRules;
var
  I, R: Integer;
  Name, Proto, Ports: string;
begin
  for I := 0 to RuleCount - 1 do
  begin
    GetRule(I, Name, Proto, Ports);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="' + Name + '"', '', SW_HIDE, ewWaitUntilTerminated, R);
  end;
end;

procedure AddFirewallRules;
var
  I, R: Integer;
  Name, Proto, Ports, Args: string;
begin
  RemoveFirewallRules;
  for I := 0 to RuleCount - 1 do
  begin
    GetRule(I, Name, Proto, Ports);
    Args := 'advfirewall firewall add rule name="' + Name + '" dir=in action=allow enable=yes profile=any protocol=' + Proto +
            ' localport=' + Ports + ' remoteip=' + DirectLinkSubnet;
    if not Exec(ExpandConstant('{sys}\netsh.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, R) or (R <> 0) then
      Log('防火牆規則失敗:' + Name + '(結束碼 ' + IntToStr(R) + ')')
    else
      Log('防火牆規則:' + Name + ' ' + Proto + ' ' + Ports + ' ← ' + DirectLinkSubnet);
  end;
end;

// 安裝位置檢查:不接受使用者資料夾({sd}\Users\…)與 Program Files(程式會寫入自己的資料夾:log、紀錄、state\,且同步服務需替換 app\)
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: string;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := AddBackslash(Lowercase(WizardDirValue));
    if (Pos(AddBackslash(Lowercase(ExpandConstant('{sd}\Users'))), Dir) = 1) or
       (Pos(AddBackslash(Lowercase(ExpandConstant('{commonpf}'))), Dir) = 1) or
       (Pos(AddBackslash(Lowercase(ExpandConstant('{commonpf32}'))), Dir) = 1) then
    begin
      MsgBox('安裝位置不可在使用者資料夾(' + ExpandConstant('{sd}\Users') + ')或 Program Files 之下:' + #13#10 +
             '程式、練習紀錄、log 與設定全部留在安裝根目錄內(規劃書第 11.2 節),請選 D:\Simosa BRM、G:\Simosa BRM 或其他資料磁碟的資料夾。', mbError, MB_OK);
      Result := False;
    end
    else if Pos(AddBackslash(Lowercase(ExpandConstant('{sd}'))), Dir) = 1 then
    begin
      // 系統磁碟(通常為 C 槽):練習紀錄、log、設定都會寫在這裡;守則為本機資料放 D: 或 G:,放系統磁碟須經訓練負責人同意。
      // 靜默安裝(/SILENT、/VERYSILENT)以 /ALLOWSYSTEMDRIVE=1 表示已取得同意;未帶此參數且訊息被抑制時視為未同意。
      if ExpandConstant('{param:ALLOWSYSTEMDRIVE|0}') <> '1' then
      begin
        if SuppressibleMsgBox('安裝位置在系統磁碟(' + ExpandConstant('{sd}') + '):練習紀錄、log 與設定都會寫在系統磁碟。' + #13#10 +
                              '守則:本機資料放 D: 或 G:;放在系統磁碟須先取得訓練負責人同意。' + #13#10 + #13#10 +
                              '已取得同意並要繼續安裝在這裡嗎?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
          Result := False;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Msg: string;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('firewall') then
      AddFirewallRules;
    InstallBundledRuntime('dotnet-runtime-8*-win-x64.exe', 'Microsoft.NETCore.App', '.NET 8 執行階段');
    InstallBundledRuntime('windowsdesktop-runtime-8*-win-x64.exe', 'Microsoft.WindowsDesktop.App', '.NET 8 Desktop Runtime');
    Msg := '';
    if not DotNetRuntimePresent('Microsoft.NETCore.App') then
      Msg := Msg + '• .NET 8 執行階段(Microsoft.NETCore.App 8.x):核心、雷達、網頁伺服、simosa-pkg 需要;離線安裝檔 dotnet-runtime-8.x-win-x64.exe' + #13#10;
    if not DotNetRuntimePresent('Microsoft.WindowsDesktop.App') then
      Msg := Msg + '• .NET 8 Desktop Runtime(Microsoft.WindowsDesktop.App 8.x):WebView2 操船台外殼需要;離線安裝檔 windowsdesktop-runtime-8.x-win-x64.exe' + #13#10;
    if not WebView2RuntimeInstalled then
      Msg := Msg + '• WebView2 Runtime:外殼需要;離線安裝檔放 tools\webview2\(第 4.5 節第 1 項),或以 Microsoft Edge 啟動(不加 -Shell;專屬設定檔 build\browser\edge)' + #13#10;
    if (Msg <> '') and not WizardSilent then
      MsgBox('尚未偵測到下列執行階段(船上完全離線,請以離線安裝檔安裝;docs\ops\安裝與啟動.md 第 1、4.5 節):' + #13#10 + Msg, mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFirewallRules;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('已解除安裝 Simosa BRM。練習紀錄與設定保留在安裝根目錄:' + #13#10 +
           ExpandConstant('{app}') + '\build\records、data\records、state\、store\、config\' + #13#10 +
           '瀏覽器端資料(草稿、參與者資料、訓練紀錄)與下載的檔案也保留:build\webview2、build\browser、build\downloads' + #13#10 +
           '不再需要時請先依 docs\ops\安裝與啟動.md 第 5 節回傳紀錄,再自行刪除。', mbInformation, MB_OK);
end;
