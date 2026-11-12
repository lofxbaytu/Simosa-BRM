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
| 安裝位置 | 預設 `D:\Simosa BRM`,沒有 D: 時 `G:\Simosa BRM`,兩者皆無才是系統磁碟根目錄下的 `Simosa BRM`;可改。**裝在系統磁碟(通常 C:)須明確確認**(守則:本機資料放 D: 或 G:;放 C: 須經訓練負責人同意;確認對話框預設「否」,靜默安裝須加 `/ALLOWSYSTEMDRIVE=1`:未加時,同時帶 `/SUPPRESSMSGBOXES` 會中止,否則仍會跳出確認框;預設位置只以「磁碟是否存在」判斷,D: 或 G: 若是光碟機、讀卡機或放安裝檔的隨身碟,請在選擇目錄頁改選)。**拒絕** `C:\Users\…` 與 Program Files 之下(程式會寫入自己的資料夾:log、紀錄、`state\`,同步服務需替換 `app\`)。整個安裝根目錄授與 Users 修改權限(船上帶領者為標準使用者)。 |
| 內容 | 發行版目錄原樣(`manifest.json`、`app\`、`tools\`、`data\`、`contracts\`、`docs\`、`packages\`、`README.txt`);`config\*.json` 已存在者不覆寫、解除安裝不刪;另建 `build\logs`、`build\records`、`build\webview2`、`data\records`、`state\`、`store\`、`config\trusted-keys`、`config\device` |
| 捷徑 | 開始功能表「Simosa BRM」與(可選)桌面:操船台(`start-ship-laptop.ps1 -Shell`,WebView2 外殼)、教官站(`start-instructor-laptop.ps1 -CheckWifi`)、停止(`stop-all.ps1`)、安裝與啟動說明、解除安裝;皆以 `powershell.exe -ExecutionPolicy Bypass -File` 啟動 |
| 防火牆(可選,預設勾選) | 入站規則「Simosa BRM …」:TCP 8765、8766、8767、8768、8080、7300,UDP 7001、60001、10110–10112;`remoteip=192.168.77.0/24`、`profile=any`(與安裝文件第 2.2 節相同);解除安裝時移除 |
| 執行階段 | `tools\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe` 存在且登錄檔無 Runtime 時 `/silent /install`;`tools\dotnet\dotnet-runtime-8*-win-x64.exe`、`windowsdesktop-runtime-8*-win-x64.exe` 存在且缺對應執行階段時 `/install /quiet /norestart`;仍缺者在結尾列出 |
| 安裝後 | 可選執行 `start-ship-laptop.ps1 -CheckOnly`(列出找到的元件)與 `simosa-pkg release verify --dir .`(清單核對;解除安裝檔 `tools\uninstall\unins000.*` 會被 verify 略過) |
| 解除安裝 | 移除安裝程式放進去的檔案與 `build\logs`、`build\tmp`、防火牆規則;**保留** `build\records`、`data\records`、`state\`、`store\`、`config\`,以及瀏覽器端資料 `build\webview2`、`build\browser` 與 `build\downloads`(結尾提示路徑) |
| 不寫 C:\ | 解除安裝檔在 `{app}\tools\uninstall\`;登錄只寫 HKLM 的解除安裝資訊;安裝過程的暫存檔用 Windows 的 `%TEMP%`(結束即刪) |

升級:船上以簽章軟體更新包(`simosa-pkg`;`docs/design/packaging-sync.md`)更新 `app\`,不重跑安裝程式;重新執行安裝程式只用於初次安裝或整機重建(同一 `AppId`,會記住先前位置;已修改的 `config\` 保留)。

## 靜態檢查(容器/CI)

`python3 -I tools/installer/check-iss.py tools/installer/SimosaBRM.iss`(`tools/run-ps-tests.sh` 一併執行):解析區段與必要鍵、`[Files]` 來源/目的地、捷徑、防火牆埠與網段、WebView2 條件、`[UninstallDelete]` 不含紀錄、前置處理 `#if/#endif` 與 `[Code]` 的 `begin/end` 配對、註解外不得出現 `C:\`。只能抓結構性錯誤,不能取代 ISCC 編譯。
