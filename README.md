# Simosa BRM — FS Bitumen No.1 / No.2 駕駛台資源管理操船模擬器

中塑海運兩艘瀝青船(FS Bitumen No.1 中塑油品壹號、FS Bitumen No.2 中塑油品貳號)專屬的 Windows 桌面版駕駛台操船模擬器,用於公司內部 BRM 訓練(複訓、新進船員船舶熟悉、靠離泊預演、事故案例重演);課程內容與評估對齊 STCW、IMO 示範課程 1.22/1.39、OCIMF TMSA 3 / SIRE 2.0 與航港局「領導統御與駕駛臺資源管理」課程大綱,但不作為航港局核定課程的模擬機(見規劃書第 1.4 節)。

- 規劃書:[docs/Simosa-BRM-操船模擬器規劃書.md](docs/Simosa-BRM-操船模擬器規劃書.md)(需求 R1–R14、決策 D1–D20)
- 開發守則:[CLAUDE.md](CLAUDE.md)
- 版本:`VERSION`(目前 2026.11.27(年.月.輪次);變更紀錄 [CHANGELOG.md](CHANGELOG.md),每輪一條;發行組裝 `tools/build-release.*` 無 `--version` 時讀此檔)

## 目前狀態(第二十七輪,2026-11-11)

**已實作(軟體,含自動測試;容器與開發機可跑)**:C# 50 Hz 確定性核心(MMG 3-DOF、拖船/錨/繫纜/碰墊、岸壁、潮汐/時變與空間流場、浪漂移力、船間交互作用、解耦橫搖、浪中升沉/縱搖(只輸出);C#↔Python 黃金夾具)、感測器故障層與 BAM/BNWAS、情境引擎(時間表/評估/參數集;E01–E22(含變體)、F01–F03 共 38 個情境)、目標船/AIS/COLREG、紀錄/重播/重新評分、雷達/ARPA 服務與 PPI 頁、NMEA/AIS 閘道與 `nmea_check` 檢驗、網頁操船台/教官站(完整/簡化/重播、靠離泊預演組題、情境編輯器與海圖點選)/報告站/簡化 ECDIS、內建 VHF 角色扮演與練習錄影、3D 視景(Godot,品質分級)、WebView2 外殼、USB 序列操控台服務與 Teensy 參考韌體、簽章更新包/紀錄包/直連同步、發行組裝與 Inno Setup 腳本、啟動腳本、Playwright 端對端、長時間穩定性(soak)測試工具。

**需實機/實資料才能完成**(`docs/驗收/需求追溯表.md` 第 3 節):內顯筆電視景幀率與 2 小時連續(soak 工具已可實跑)、ENC 與港方座標校正、實船 3D 模型、FSB2 試俥資料與各物理係數校正、OpenCPN 顯示、WebView2 外殼/安裝程式/VHF 麥克風/錄影的 Windows 實機驗證、操控台硬體製作與韌體燒錄、評估者校準與課程試行、三份訓練文件核准。

**如何開始**:`git clone` 到 `D:\Simosa BRM` → 系統管理員 PowerShell 執行 `.\tools\setup-windows.ps1`(工具與快取設定,見下節)→ `dotnet build SimosaBRM.sln -c Release` 與 `cd src\Instruments.Web && pnpm install && pnpm build` → 啟動 `.\tools\launch\start-ship-laptop.ps1`(船上)或 `.\tools\launch\start-instructor-laptop.ps1 -Standalone`(單機教官站),瀏覽器開 `http://127.0.0.1:8080/instructor.html?mode=simple`;結束用 `.\tools\launch\stop-all.ps1`。發行版改用 `.\tools\build-release.ps1` 產出 `build\release\<版本>\` 後依 `docs/ops/安裝與啟動.md` 安裝。

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

它會設定使用者環境變數(NUGET_PACKAGES、DOTNET_CLI_HOME、UV_CACHE_DIR、UV_PYTHON_INSTALL_DIR、UV_TOOL_DIR、PLAYWRIGHT_BROWSERS_PATH 等指到 `.cache\`)並檢查工具版本。需要的工具:.NET 8 SDK、Node.js 22 LTS(pnpm)、uv、Git + Git LFS;安裝程式本身可選擇裝到 `D:\Tools\`。Python 3.12+ 不必另外安裝:機器上沒有時 `uv sync` 會自動下載受管 CPython,setup-windows.ps1 以 UV_PYTHON_INSTALL_DIR 把它放在 `.cache\uv-python\`(uv 預設為 `%APPDATA%\uv\python`,即 `C:\`,而且只能用環境變數改)。uv 的相對 `cache-dir` 設定是以執行時的目前目錄解析,因此 uv 指令請在專案根目錄或 `src\Tools.Calibration` 下執行(或從別處用 `uv --directory src\Tools.Calibration …`);執行過 setup-windows.ps1 後 UV_CACHE_DIR 為絕對路徑,不受此限。

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
tools/                        Windows 設定腳本、發行組裝(build-release.*)、啟動腳本(launch/)、simosa-pkg 包裝
config/                       安裝設定範例(不含金鑰)
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
uv run pytest -q                                           # 暫存檔在 build/test-tmp/pytest、快取在 .cache/pytest(任何目前目錄皆同)

# 網頁儀器
cd src/Instruments.Web
pnpm install
pnpm dev            # http://localhost:5173/  (?demo=1 為示範模式;預設連 ws://localhost:8765)
pnpm build && pnpm test
```

## 建置發行版與安裝

```powershell
.\tools\build-release.ps1                        # Windows(win-x64);版本讀 VERSION 檔(-Version 2026.11.1 可覆寫);Linux/容器:tools/build-release.sh [--version …]
```
建置全部(sln Release、網頁)、`dotnet publish`(framework-dependent)到 `build\release\<版本>\`:`app\{simcore,radar,web,web-server,launch,visual}`、`tools\updater`(simosa-pkg)、`data\`(不含 `ships\*\confidential`)、`contracts\`、`docs\`、`config\` 範例、`manifest.json`(每檔 SHA-256、git 版次、元件版本)與簽章軟體更新包 `packages\*.sbupd`(開發金鑰,僅供測試);Godot 視景在有匯出範本時匯出,否則記入清單註記。啟動腳本 `tools\launch\start-ship-laptop.ps1`、`start-instructor-laptop.ps1`、`stop-all.ps1`(與 `.sh` 版)依 `config\ship-laptop.json`、`config\instructor-laptop.json` 啟動核心、雷達、網頁、視景與直連同步服務(啟動前檢查埠占用;`stop-all -KeepSync`/`--keep-sync` 於推送軟體更新包時保留同步服務);啟動腳本測試:`tools/launch/tests/launch-tests.sh`、`pwsh -File tools/launch/tests/launch-common.tests.ps1`。

- 安裝與啟動(船上筆電、教官筆電、更新包與紀錄回傳、故障排除):[docs/ops/安裝與啟動.md](docs/ops/安裝與啟動.md)
- 需求追溯表(R1–R14、第 14 章驗收表 → 模組/測試/狀態、需實機項目):[docs/驗收/需求追溯表.md](docs/驗收/需求追溯表.md)
- 更新包、紀錄包與直連同步設計:[docs/design/packaging-sync.md](docs/design/packaging-sync.md)
- 修改指南(想改什麼 → 改哪裡 → 跑哪個測試;三級分類、契約同步清單、夾具重錄、熱點檔導覽):[docs/修改指南.md](docs/修改指南.md)

## 測試

四個層級(`docs/design/testing.md`):黃金/夾具(試俥公差、C#↔Python、確定性雜湊)、單元(`dotnet test`、`pnpm test`、`uv run pytest`)、子程序(Host 實跑/重播、雷達 WebSocket、NMEA 檢驗、啟動腳本)、端對端(真實 Chromium + 真實核心)。

```bash
. /etc/profile.d/dotnet.sh && dotnet build SimosaBRM.sln -c Release && dotnet test tests/SimCore.Tests -c Release --no-build
cd src/Instruments.Web && pnpm test && pnpm exec tsc --noEmit -p .
cd src/Tools.Calibration && uv run pytest -q
bash tools/launch/tests/launch-tests.sh && bash tools/run-ps-tests.sh
bash tools/run-e2e.sh            # 端對端:建置網頁與 .NET → Playwright(tests/e2e;操船台、教官站載入/拒絕/舵令/凍結、VHF 文字與 PTT、錄影、雷達、ECDIS、報告、情境編輯器)→ 摘要;輸出 build/e2e/
bash tools/run-e2e.sh --skip-build -- --grep 雷達   # 已建置時只跑部分案例
bash tools/soak-test.sh --minutes 120 --clients 3   # 長時間穩定性(soak):核心 + 雷達 + 網頁 + N 個合成客戶端 + 學員腳本,每 10 s 取樣 CPU/RSS/steps/s/掉拍,
                                                    # 結束寫 build/soak/report-<時間>.md/.json 並判定(50 Hz 無掉拍、延遲 p95 < 50 ms、RSS 不持續成長);Windows:tools\soak-test.ps1
```

端對端用 Playwright 管理的 Chromium(`PLAYWRIGHT_BROWSERS_PATH`,容器為 `/opt/pw-browsers`,CI 裝到 `.cache/pw-browsers`),失敗的截圖與 trace 在 `build/e2e/test-results/`,HTML 報告 `pnpm --dir tests/e2e report`。

## 持續整合

GitHub Actions(`.github/workflows/ci.yml`):Python 測試(Ubuntu)、網頁建置與測試(Ubuntu)、.NET 建置與測試(Windows)、端對端(Ubuntu;Playwright + 真實核心,`tools/run-e2e.sh`,失敗時上傳截圖/trace/log)。
