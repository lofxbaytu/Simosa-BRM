# Simosa BRM — FS Bitumen No.1 / No.2 駕駛台資源管理操船模擬器

中塑海運兩艘瀝青船(FS Bitumen No.1 中塑油品壹號、FS Bitumen No.2 中塑油品貳號)專屬的 Windows 桌面版駕駛台操船模擬器,用於公司內部 BRM 訓練(複訓、新進船員船舶熟悉、靠離泊預演、事故案例重演);課程內容與評估對齊 STCW、IMO 示範課程 1.22/1.39、OCIMF TMSA 3 / SIRE 2.0 與航港局「領導統御與駕駛臺資源管理」課程大綱,但不作為航港局核定課程的模擬機(見規劃書第 1.4 節)。

- 規劃書:[docs/Simosa-BRM-操船模擬器規劃書.md](docs/Simosa-BRM-操船模擬器規劃書.md)(需求 R1–R14、決策 D1–D20)
- 開發守則:[CLAUDE.md](CLAUDE.md)
- 目前狀態:**第 1 階段(垂直切片)實作中**

## 已決定的事項(摘要)

- 用途:公司內部 BRM 訓練;不申請航港局核定;第三方認證列為日後選項。
- 部署:兩船船上各 1 台一般文書筆電 + 外接螢幕 + USB 操控台(完全離線,與航儀網路隔離;視景品質可調,內顯即可運行);訪船船長的教官筆電以一條網路線直連即為教官站並同步更新與紀錄,不設岸上伺服器。
- 訓練:負責人與主要教官為訪船船長;複訓每 5 年與法定證書換證同步;訓練程序納入 SMS,程序內容可由岸上更新並派送到船。
- 船模:以造船廠交船文件、No.1 滿載試俥實測值與兩船 IMO 駕駛台海報校正(規劃書第 3.5 節、附錄 F)。

## 開發資料夾與磁碟

請把本儲存庫 clone 到 **`D:\Simosa BRM`**。所有建置輸出(`build\`)、套件與快取(`.cache\`)、船模與圖資(`data\`)、紀錄(`build\records\`)都留在這個資料夾內,不寫到 `C:\`;相關設定在 `Directory.Build.props`、`nuget.config`、`.npmrc`、`uv.toml`。第一次使用先以系統管理員 PowerShell 執行:

```powershell
cd "D:\Simosa BRM"
.\tools\setup-windows.ps1
```

它會設定使用者環境變數(NUGET_PACKAGES、DOTNET_CLI_HOME、UV_CACHE_DIR、PLAYWRIGHT_BROWSERS_PATH 等指到 `.cache\`)並檢查工具版本。需要的工具:.NET 8 SDK、Node.js 22 LTS(pnpm)、uv(Python 3.12+)、Git + Git LFS;安裝程式本身可選擇裝到 `D:\Tools\`。

## 儲存庫結構

```
SimosaBRM.sln                 .NET 方案(SimCore、SimCore.Host、Gateway.Nmea、SimCore.Tests)
src/
  Contracts/                  各程式共用的 JSON Schema(自船狀態、指令、船舶資料、係數、情境)
  SimCore/                    C# 模擬核心:50 Hz 確定性引擎、RK4、致動器、環境、情境、紀錄/重播
  SimCore.Host/               主控程式:WebSocket 狀態伺服器(8765)、UDP 多播、NMEA 閘道
  Gateway.Nmea/               NMEA 0183 句型產生(GGA/RMC/VTG/HDT/ROT/VBW/DPT/MWV/RSA)
  Tools.Calibration/          Python 參考 MMG 模型、係數估計、試俥參數識別、驗證報告、WebSocket 伺服器
  Instruments.Web/            網頁儀器(Vite + TypeScript + Lit):conning 顯示、鳥瞰航跡、操船台
tests/SimCore.Tests/          C# 測試(xunit)
data/
  ships/FSB1, FSB2/           particulars.json(交船文件)、trial_targets.json(試俥/海報目標)、coefficients.*.json(識別結果)
  scenarios/                  情境 YAML
docs/                         規劃書與研究資料
tools/                        Windows 設定腳本
```

## 執行

```powershell
# C# 核心(暫代動力模型;MMG 移植中)— 啟動後網頁儀器可直接連線
dotnet build SimosaBRM.sln
dotnet run --project src/SimCore.Host -- --ship FSB1 --loading ballast --scenario data/scenarios/E01_baseline.yaml --record
dotnet test SimosaBRM.sln

# Python 參考模型與校正(FSB1 以試俥實測識別)
cd src/Tools.Calibration
uv sync
uv run simosa-brm identify --ship FSB1 --loading full
uv run simosa-brm validate --ship FSB1 --loading full     # 報告在 build/calibration/FSB1/
uv run simosa-brm serve --ship FSB1 --port 8765            # 給網頁儀器用的 WebSocket
uv run pytest -q

# 網頁儀器
cd src/Instruments.Web
pnpm install
pnpm dev            # http://localhost:5173/  (?demo=1 為示範模式;預設連 ws://localhost:8765)
pnpm build && pnpm test
```

## 持續整合

GitHub Actions(`.github/workflows/ci.yml`):Python 測試(Ubuntu)、網頁建置與測試(Ubuntu)、.NET 建置與測試(Windows)。
