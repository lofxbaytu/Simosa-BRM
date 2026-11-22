# Windows 安裝程式(Inno Setup 6.3+)

> 規劃書第 5.5 節「船上部署」、第 11.2 節資料夾結構、第 12.1 節 M4 交付「安裝程式」;安裝步驟與驗收見 `docs/ops/安裝與啟動.md` 第 2.8 節;需求 R13。
> 腳本 `SimosaBRM.iss` 已可編譯(靜態檢查通過),**但只在 Linux 容器做靜態檢查,尚未在 Windows 實機以 ISCC 編譯與安裝驗證**(需求追溯表第 4 節)。

## 產生安裝程式

1. 開發機(Windows)安裝 Inno Setup 6.3 以上(可裝到 `D:\Tools\Inno Setup 6\`;或把可攜版放在 `.cache\innosetup\ISCC.exe`;`ISCC` 環境變數亦可指定),不需網際網路。
2. 先產生發行版:`.\tools\build-release.ps1 -Version 2026.11.1 -Installer`(`-Installer` 會在步驟 7 的清單核對之後呼叫 `ISCC.exe`):
   ```
   ISCC.exe /DAppVersion=2026.11.1 /DSourceDir="D:\Simosa BRM\build\release\2026.11.1" /DOutputDir="D:\Simosa BRM\build\release\installer" tools\installer\SimosaBRM.iss
   → build\release\installer\SimosaBRM-Setup-2026.11.1.exe
   ```
   找不到 ISCC 時(或 Linux 的 `build-release.sh --installer`)只把腳本、本說明與 `BUILD-<版本>.txt`(上述指令)複製到 `build\release\installer\`,之後在有 Inno Setup 的機器執行即可;發行版本身也帶著 `tools\installer\SimosaBRM.iss`。
3. 可選的離線執行階段:把 `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` 放到發行版 `tools\webview2\`、`dotnet-runtime-8.x.x-win-x64.exe` / `windowsdesktop-runtime-8.x.x-win-x64.exe` 放到 `tools\dotnet\` **再**編譯,安裝程式會在尚未安裝時一併靜默安裝(全部離線)。
4. `-Version` 須為 1–4 段數字(與更新包相同);`/DDirectLinkSubnet=192.168.77.0/24` 可改防火牆允許的直連網段。
5. 正式派送前以公司憑證對 `SimosaBRM-Setup-<版本>.exe` 做程式碼簽章(規劃書第 10 章「安裝與 CI」;尚未納入腳本)。

## 安裝程式行為

| 項目 | 行為 |
|---|---|
| 安裝位置 | 預設 `D:\Simosa BRM`,沒有 D: 時 `G:\Simosa BRM`(先找固定磁碟;其次不是安裝程式所在的抽取式磁碟;光碟機不用),都沒有才是系統磁碟根目錄下的 `Simosa BRM`;可改。**裝在系統磁碟(通常 C:)須明確確認**(守則:本機資料放 D: 或 G:;放 C: 須經訓練負責人同意;確認對話框預設「否」,靜默安裝須加 `/ALLOWSYSTEMDRIVE=1`:未加時,同時帶 `/SUPPRESSMSGBOXES` 會中止,否則仍會跳出確認框;預設位置只以「磁碟是否存在」判斷,D: 或 G: 若是光碟機、讀卡機或放安裝檔的隨身碟,請在選擇目錄頁改選)。**拒絕** `C:\Users\…` 與 Program Files 之下(程式會寫入自己的資料夾:log、紀錄、`state\`,同步服務需替換 `app\`)。整個安裝根目錄授與 Users 修改權限(船上帶領者為標準使用者)。 |
| 內容 | 發行版目錄原樣(`manifest.json`、`app\`、`tools\`、`data\`、`contracts\`、`docs\`、`packages\`、`README.txt`),不含 `*.pdb`、`Thumbs.db`(`[Files]` 的 `Excludes`;這份清單必須與 simosa-pkg 的 `ReleaseManifestBuilder.InstallerExcludedPatterns` 相同,`release manifest` 遇到 `*.pdb` 會失敗,兩邊要一起改);`config\*.json` 已存在者不覆寫、解除安裝不刪;另建 `build\logs`、`build\records`、`build\webview2`、`data\records`、`state\`、`store\`、`config\trusted-keys`、`config\device` |
| 重新安裝 / 升級 | `[InstallDelete]`:安裝前先清空程式目錄 `app\` 與 `tools\updater\`(只這兩項),舊版留下而新版沒有的程式檔(檔名帶雜湊的網頁資產、先前更新包新增的檔案)不會殘留,安裝後這兩個資料夾與發行版完全一致,`release verify --installed` 不需手動清理即通過;`config\`、`data\`、`state\`、`store\`、`build\` 不動。安裝前先以 `stop-all.ps1` 結束全部程序(執行中的檔案刪不掉) |
| 捷徑 | 開始功能表「Simosa BRM」與(可選)桌面:操船台(`start-ship-laptop.ps1 -Shell`,WebView2 外殼)、教官站(`start-instructor-laptop.ps1 -CheckWifi`)、停止(`stop-all.ps1`)、安裝與啟動說明、解除安裝;皆以 `powershell.exe -ExecutionPolicy Bypass -File` 啟動 |
| 防火牆(可選,預設勾選) | 入站規則「Simosa BRM …」:TCP 8765、8766、8767、8768、8080、7300,UDP 7001、60001、10110–10112;`remoteip=192.168.77.0/24`、`profile=any`(與安裝文件第 2.2 節相同);解除安裝時移除 |
| 執行階段 | `tools\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe` 存在且登錄檔無 Runtime 時 `/silent /install`;`tools\dotnet\dotnet-runtime-8*-win-x64.exe`、`windowsdesktop-runtime-8*-win-x64.exe` 存在且缺對應執行階段時 `/install /quiet /norestart`;仍缺者在結尾列出 |
| 安裝後 | 可選執行 `start-ship-laptop.ps1 -CheckOnly`(列出找到的元件)與 `simosa-pkg release verify --dir . --installed`(安裝模式核對:`manifest.json` 疊上更新包覆蓋清單 `state\installed-files.json`;`build\`、`state\`、`config\device\` 等執行期資料與解除安裝檔 `tools\uninstall\unins000.*` 不算多出,修改過的 `config\*.json` 只列提醒,程式/資料檔缺少或雜湊不符、`app\` 內多出檔案為錯誤並說明下一步;使用中隨時可再執行,短指令 `simosa-pkg verify-install`。不加 `--installed` 的一般模式只適用剛複製好的發行版) |
| 解除安裝 | 移除安裝程式放進去的檔案與 `build\logs`、`build\tmp`、防火牆規則;**保留** `build\records`、`data\records`、`state\`、`store\`、`config\`,以及瀏覽器端資料 `build\webview2`、`build\browser` 與 `build\downloads`(結尾提示路徑) |
| 不寫 C:\ | 解除安裝檔在 `{app}\tools\uninstall\`;登錄只寫 HKLM 的解除安裝資訊;開始功能表與桌面捷徑(`.lnk`)在 Windows 的公用位置(`C:\ProgramData\…\Start Menu`、公用桌面),屬應用程式的一部分;安裝過程的暫存檔用 Windows 的 `%TEMP%`(結束即刪) |

升級:平時船上以簽章軟體更新包(`simosa-pkg`;`docs/design/packaging-sync.md`)更新 `app\`,不重跑安裝程式;重新執行安裝程式用於初次安裝、整機重建、程式檔損毀(`release verify --installed` 的「下一步」會寫明所需的安裝程式版本)或換用新版 simosa-pkg(同一 `AppId`,會記住先前位置;已修改的 `config\` 保留;程式目錄先清空再安裝,見上表)。以壓縮檔或手動複製覆蓋發行版時沒有清空這一步,舊檔會被 `release verify --installed` 列為「程式目錄內多出的檔案」,依清單刪除。

## 靜態檢查(容器/CI)

`python3 -I tools/installer/check-iss.py tools/installer/SimosaBRM.iss`(`tools/run-ps-tests.sh` 一併執行):解析區段與必要鍵、`[Files]` 來源/目的地與 `Excludes`(檔名樣式須與 `src/Tools.Packaging/Release/ReleaseManifest.cs` 的 `InstallerExcludedPatterns` 相同;檢查直接讀該原始碼)、`[InstallDelete]` 只能是 `{app}\app` 與 `{app}\tools\updater`(白名單)、捷徑、防火牆埠與網段、WebView2 條件、`[UninstallDelete]` 不含紀錄、前置處理 `#if/#endif` 與 `[Code]` 的 `begin/end` 配對、註解外不得出現 `C:\`。只能抓結構性錯誤,不能取代 ISCC 編譯。
