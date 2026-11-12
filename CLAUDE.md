# Simosa BRM 操船模擬器 — 開發守則

- 規劃書:`docs/Simosa-BRM-操船模擬器規劃書.md`(需求 ID R1–R14、決策 D1–D20、章節編號在程式註解與提交訊息中引用)。
- 開發資料夾放在 **D: 或 G:**(Windows;例如 `D:\Simosa BRM`、`G:\Simosa BRM`,程式不寫死磁碟代號)。**本機資料守則(使用者指示,2026-11-12)**:所有要儲存在本機的資料(建置輸出、快取、套件、暫存、紀錄、設定、瀏覽器端資料)都放在專案(安裝)資料夾內,即 D: 或 G:;**C: 只放應用程式本身**(.NET、Node.js、Git、Edge/WebView2 Runtime 等);某項目放在隨身碟會嚴重拖慢進度時,**必須先取得使用者同意**才可放 C:(不得自行決定;例外開關見 `docs/ops/本機資料位置.md`)。位置:`build\`(輸出、`build\tmp` 暫存、`build\records` 紀錄、`build\browser`/`build\webview2` 瀏覽器端資料)、`.cache\`(NuGet/pnpm/uv/Playwright/Godot)、`data\`(船模、圖資、情境)。相關設定在 `Directory.Build.props`、`nuget.config`、`.npmrc`、`uv.toml`、`tools/setup-windows.ps1`、`tools/launch/launch-common.ps1`;實機檢查 `tools/check-local-data.ps1`;開發終端機用 `tools/dev-shell.cmd`(或 `setup-windows.ps1 -SessionOnly`);開發用的磁碟須為 NTFS(exFAT/FAT32 上 `pnpm install` 會失敗);pnpm 固定 10.28.0(11 以上不讀 `.npmrc` 的快取位置)。雲端容器比照:不寫 `/tmp` 與家目錄,暫存放 `build/tmp/`。
- 語言:C#/.NET 8(核心、閘道、感測器)、TypeScript(儀器、教官站、講評站;Vite + Lit)、Python 3.12+(校正與參考實作;uv 管理)。
- 單位:核心內部 SI 與弧度;對外介面依 `src/Contracts/` 的 JSON Schema(度、節、航向 0–360、舵角右正左負)。
- 確定性:固定步長 50 Hz RK4,所有亂數由情境種子產生;相同輸入紀錄必須得到相同狀態雜湊(第 14 章驗收)。
- 船舶資料:`data/ships/FSB1|FSB2/particulars.json`(交船文件)、`trial_targets.json`(試俥/海報目標值;No.1 為實測,No.2 為海報讀值)。係數檔 `coefficients.*.json` 由 `src/Tools.Calibration` 產生,C# 與 Python 讀同一份。
- 視景以內顯文書筆電為效能基準(低/中/高品質分級,規劃書第 7.3 節)。
- 船上部署:船上筆電完全離線;教官筆電以網路線直連;更新包與紀錄包必須簽章(規劃書第 5.5 節)。
- 測試:`pytest`(Python)、`dotnet test`(C#)、`pnpm test`(Web);黃金測試以 `data/ships/*/trial_targets.json` 的公差為準。
- 語言與文件:程式註解與使用者文件用繁體中文(專有名詞保留英文),識別字用英文。
- 不做:不連網際網路取資料、不把本機資料寫入 `C:\`(未經使用者同意)、不把機密係數放到公開儲存庫以外的地方。
- PowerShell 腳本(`*.ps1`)一律存成 **UTF-8 with BOM**、行尾 CRLF(Windows PowerShell 5.1 對無 BOM 檔案以 Big5 讀取;BOM 由 `tools/pscheck.py` 檢查,CRLF 由 `.gitattributes` 保證;雙引號字串內變數後面緊接中文或半形冒號時寫 `${變數}`)。
