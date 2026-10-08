# Simosa BRM 操船模擬器 — 開發守則

- 規劃書:`docs/Simosa-BRM-操船模擬器規劃書.md`(需求 ID R1–R14、決策 D1–D20、章節編號在程式註解與提交訊息中引用)。
- 開發資料夾為 `D:\Simosa BRM`(Windows);**所有建置輸出、快取、套件、紀錄、資料都必須留在專案資料夾內**,不得寫到 `C:\`:`build\`(輸出)、`.cache\`(NuGet/pnpm/uv/Playwright)、`data\`(船模、圖資、情境)。相關設定已在 `Directory.Build.props`、`nuget.config`、`.npmrc`、`uv.toml`、`tools/setup-windows.ps1`。
- 語言:C#/.NET 8(核心、閘道、感測器)、TypeScript(儀器、教官站、講評站;Vite + Lit)、Python 3.12+(校正與參考實作;uv 管理)。
- 單位:核心內部 SI 與弧度;對外介面依 `src/Contracts/` 的 JSON Schema(度、節、航向 0–360、舵角右正左負)。
- 確定性:固定步長 50 Hz RK4,所有亂數由情境種子產生;相同輸入紀錄必須得到相同狀態雜湊(第 14 章驗收)。
- 船舶資料:`data/ships/FSB1|FSB2/particulars.json`(交船文件)、`trial_targets.json`(試俥/海報目標值;No.1 為實測,No.2 為海報讀值)。係數檔 `coefficients.*.json` 由 `src/Tools.Calibration` 產生,C# 與 Python 讀同一份。
- 視景以內顯文書筆電為效能基準(低/中/高品質分級,規劃書第 7.3 節)。
- 船上部署:船上筆電完全離線;教官筆電以網路線直連;更新包與紀錄包必須簽章(規劃書第 5.5 節)。
- 測試:`pytest`(Python)、`dotnet test`(C#)、`pnpm test`(Web);黃金測試以 `data/ships/*/trial_targets.json` 的公差為準。
- 語言與文件:程式註解與使用者文件用繁體中文(專有名詞保留英文),識別字用英文。
- 不做:不連網際網路取資料、不寫入 `C:\`、不把機密係數放到公開儲存庫以外的地方。
